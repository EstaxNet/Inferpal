using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Extensibility.Shell;

namespace Inferpal.Commands;

/// <summary>
/// Right-click "Edit with AI…" context-menu command (and Shift+Alt+I shortcut).
///
/// Runs entirely in the OOP extension host process — no MEF, no IPC:
/// <list type="number">
///   <item>Captures the active selection (or caret line when empty).</item>
///   <item>Shows a floating WPF dialog to collect the edit instruction.</item>
///   <item>While Ollama generates the response the dialog shows an animated spinner.</item>
///   <item>Applies the cleaned response via <c>Extensibility.Editor().EditAsync()</c>.</item>
/// </list>
/// </summary>
[VisualStudioContribution]
internal class InlineEditSelectionCommand : Command
{
    private readonly VsContextHolder   _contextHolder;
    private readonly InferpalConfig _config;
    private readonly IInferenceProvider _client;

    public InlineEditSelectionCommand(
        VisualStudioExtensibility extensibility,
        VsContextHolder           contextHolder,
        InferpalConfig         config,
        IInferenceProvider        client)
        : base(extensibility)
    {
        _contextHolder = contextHolder;
        _config        = config;
        _client        = client;
    }

    public override CommandConfiguration CommandConfiguration => new("%ContextMenuInlineEdit%")
    {
        Icon      = new(ImageMoniker.KnownValues.Refactoring, IconSettings.IconAndText),
        // Not Ctrl+Shift+I: the text editor's scope keeps that key for reverse incremental search, and a scope goes before
        // the Global one this binding lands in, so the command would never be reached from the editor.
        Shortcuts = [new CommandShortcutConfiguration(ModifierKey.ShiftLeftAlt, Key.I)],
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
    {
        // ── 1. Resolve the active view ────────────────────────────────────────
        var view = _contextHolder.LatestView
                ?? await Extensibility.Editor().GetActiveTextViewAsync(context, ct);
        if (view is null) return;

        // ── 2. Determine the code block to edit ───────────────────────────────
        // The selection, or the caret's line — the rule VS Code's command applies too (InlineEditPrompt.Target).
        var docText = view.Document.Text.CopyToString();
        var sel     = view.Selection;
        var target  = sel.IsEmpty
            ? Services.CodeActions.InlineEditPrompt.Target(docText, sel.InsertionPosition.Offset, sel.InsertionPosition.Offset)
            : Services.CodeActions.InlineEditPrompt.Target(docText, sel.Start.Offset, sel.End.Offset);
        if (target is not { } block)
        {
            // A blank line and no selection: nothing to edit, and a command that does nothing says why.
            await ShowInfoAsync(Strings.InlineEditNothingToEdit, ct);
            return;
        }
        var (editStart, editEnd) = block;
        var originalCode = docText[editStart..editEnd];
        var editRange    = new TextRange(new TextPosition(view.Document, editStart), new TextPosition(view.Document, editEnd));
        // Excluded by the workspace's ignore files: the code never reaches the model, and the reason is said.
        WorkspaceExclusions.Follow();
        if (Services.Governance.ContentExclusions.NoticeFor(view.Document.Uri.LocalPath, null) is { } excluded)
        {
            await ShowInfoAsync(excluded, ct);
            return;
        }

        // ── 3. Show dialog and collect instruction ────────────────────────────
        // The dialog runs on its own STA thread and stays visible as a spinner
        // while Ollama generates, giving the user clear feedback.
        InlineEditInputWindow dlg;
        try
        {
            dlg = await InlineEditInputWindow.CreateAndShowAsync();
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("InlineEdit.Dialog", ex);
            await ShowFailureAsync(ex.Message, ct);
            return;
        }

        // Completed with null when the user cancels or closes the dialog — it has closed itself.
        var instruction = await dlg.InstructionTask;
        if (string.IsNullOrWhiteSpace(instruction)) return;

        // Instruction received → switch dialog to spinner mode.
        dlg.SwitchToLoading();

        // ── 4. Call the model ─────────────────────────────────────────────────
        var model = ResolveModel();
        if (await GenerateAsync(dlg, model, BuildMessages(originalCode, instruction), ct) is not { } result) return;
        if (dlg.CancelledByUser.IsCancellationRequested) return;

        // ⚠ The SAME funnel as the slash commands and the host: the document's line endings, the no-change sentinel, the
        // named empty reply. Applied without it, a click in the margin plus Shift+Down selects the line with its ending, a
        // trimmed reply moves the next line up against the one just edited, and the unchanged echo, differing by that
        // single byte, is applied instead of being reported.
        var finished = CodeActionPipeline.Finish(
            result, originalCode, view.Document.Text.CopyToString(),
            reindent: true, model, _client.ServerAddress, view.Document.Uri.LocalPath);

        if (finished.Outcome == CodeActionOutcome.NoChangeNeeded)
        {
            await ShowInfoAsync(Strings.InlineEditNoChange, ct);
            return;
        }
        if (finished.Outcome != CodeActionOutcome.Edited)
        {
            // The spinner vanished and nothing changed: say that the model gave nothing to apply.
            await ShowFailureAsync(finished.FailureDetail, ct);
            return;
        }
        // ── 5. Apply the edit ─────────────────────────────────────────────────
        await ApplyAsync(view, docText, editStart, editEnd, editRange, finished.EditedCode!, ct);
    }

    /// <summary>
    /// Asks the model for the edit while the dialog shows its spinner, and closes the dialog whatever happens. Closing the
    /// spinner cancels the generation, or the edit lands anyway once the model answers.
    /// </summary>
    /// <returns>The model's reply; <c>null</c> when there is nothing to apply — closed by the user or VS cancelling the
    /// command (nothing to say), or a failure (said).</returns>
    private async Task<ChatTurnResult?> GenerateAsync(
        InlineEditInputWindow dlg, string model, List<ChatMessageDto> messages, CancellationToken ct)
    {
        using var generation = CancellationTokenSource.CreateLinkedTokenSource(ct, dlg.CancelledByUser);
        try
        {
            // The model's wait in the spinner window while the server reads the prompt.
            using (ModelWaitScope.Open(dlg.ShowWait))
                return await _client.SendChatAsync(
                    model, messages, EmptyToolRegistry.Instance, onToken: null, generation.Token, TaskComplexity.Quick);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("InlineEdit.Generate", ex);
            await ShowFailureAsync(ex.Message, ct);
            return null;
        }
        finally
        {
            // Always close the spinner, even on error/cancel.
            dlg.CloseFromThread();
        }
    }

    /// <summary>
    /// Applies <paramref name="editedCode"/> over <paramref name="editRange"/>. ⚠ Through the preview when "Show the change
    /// before applying it" is on — the box of THIS command's settings section. The rewrite is computed from the text the
    /// range was read from (<paramref name="docText"/>): a file changed since is refused by the renderer (drifted), never
    /// patched at offsets that moved.
    /// </summary>
    private async Task ApplyAsync(ITextViewSnapshot view, string docText, int editStart, int editEnd, TextRange editRange,
                                  string editedCode, CancellationToken ct)
    {
        try
        {
            if (await InPlaceCodeEdit.TryPreviewAsync(
                    _config, view, docText, docText[..editStart] + editedCode + docText[editEnd..], ct))
                return;
        }
        catch (OperationCanceledException) { return; }

        try
        {
            var response = await Extensibility.Editor().EditAsync(
                batch =>
                {
                    var doc = view.Document.AsEditable(batch);
                    doc.Replace(editRange, editedCode);
                },
                ct);
            // Refused, not thrown: the user kept editing that region while the spinner turned.
            if (!Services.VsIntegration.VsEditResult.Applied(response))
                await Extensibility.Shell().ShowPromptAsync(Strings.CodeActionDocumentChanged, PromptOptions.OK, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // EditAsync may fail if the document was modified between the initial snapshot and the
            // apply call. The user can retry — once told that nothing was applied.
            Diagnostics.Swallow("InlineEdit.Apply", ex);
            await ShowFailureAsync(ex.Message, ct);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>Tells the user the edit was not applied, in the same words as the other code actions.</summary>
    private async Task ShowFailureAsync(string? detail, CancellationToken ct)
    {
        try
        {
            await Extensibility.Shell().ShowPromptAsync(InPlaceCodeEdit.FailureMessage(detail), PromptOptions.OK, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Swallow("InlineEdit.Notify", ex); }
    }

    /// <summary>Tells the user the model had nothing to change — a command that does nothing is
    /// indistinguishable from one that failed.</summary>
    private async Task ShowInfoAsync(string message, CancellationToken ct)
    {
        try
        {
            await Extensibility.Shell().ShowPromptAsync(message, PromptOptions.OK, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Swallow("InlineEdit.Notify", ex); }
    }

    private string ResolveModel() => ModelRouter.Resolve(_config, ModelRole.InlineEdit);

    /// <summary>The prompt VS Code's "Edit with AI" sends too (<see cref="Services.CodeActions.InlineEditPrompt"/>).</summary>
    private static List<ChatMessageDto> BuildMessages(string originalCode, string instruction) =>
    [
        new ChatMessageDto("system", Services.CodeActions.InlineEditPrompt.System),
        new ChatMessageDto("user",   Services.CodeActions.InlineEditPrompt.UserMessage(originalCode, instruction)),
    ];

}
