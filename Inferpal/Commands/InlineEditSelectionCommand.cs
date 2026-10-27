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
/// Right-click "Edit with AI…" context-menu command (and Ctrl+Shift+I shortcut).
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
        Shortcuts = [new CommandShortcutConfiguration(ModifierKey.ControlShift, Key.I)],
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

        // ── 4. Call Ollama ────────────────────────────────────────────────────
        var model    = ResolveModel();
        var messages = BuildMessages(originalCode, instruction);

        ChatTurnResult result;
        // Closing the spinner cancels the generation, or the edit lands anyway once the model answers.
        using (var generation = CancellationTokenSource.CreateLinkedTokenSource(ct, dlg.CancelledByUser))
        {
            try
            {
                // The model's wait in the spinner window while the server reads the prompt.
                using (ModelWaitScope.Open(dlg.ShowWait))
                    result = await _client.SendChatAsync(
                        model, messages, EmptyToolRegistry.Instance, onToken: null, generation.Token, TaskComplexity.Quick);
            }
            catch (OperationCanceledException)
            {
                // Closed by the user, or VS cancelled the command: nothing to apply, nothing to say.
                return;
            }
            catch (Exception ex)
            {
                Diagnostics.Swallow("InlineEdit.Generate", ex);
                await ShowFailureAsync(ex.Message, ct);
                return;
            }
            finally
            {
                // Always close the spinner, even on error/cancel.
                dlg.CloseFromThread();
            }
        }
        if (dlg.CancelledByUser.IsCancellationRequested) return;

        // ⚠ The SAME funnel as the slash commands and the host, and it was not always: this command
        // did `Reindent(Clean(reply))` and applied it, so it missed the document's line endings, the
        // no-change sentinel, the named empty reply — and the two steps whose comment cites THIS
        // command's gesture. A click in the margin plus Shift+Down selects the line with its ending,
        // `Clean` trims it, and the next line moved up against the one just edited; the unchanged
        // echo, differing by that single byte, was applied instead of being reported.
        var finished = CodeActionPipeline.Finish(
            result, originalCode, view.Document.Text.CopyToString(),
            reindent: true, model, _client.ServerAddress);

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
        var editedCode = finished.EditedCode!;

        // ── 5. Apply the edit ─────────────────────────────────────────────────
        // ⚠ Through the preview when "Show the change before applying it" is on — the box of THIS command's settings
        // section, which it applied past. The rewrite is computed from the text the range was read from: a file changed
        // since is refused by the renderer (drifted), never patched at offsets that moved.
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
