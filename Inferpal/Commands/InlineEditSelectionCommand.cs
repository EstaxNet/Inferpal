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
        string   originalCode;
        TextRange editRange;

        var sel = view.Selection;
        if (!sel.IsEmpty)
        {
            // Expand the selection start back over leading whitespace to the beginning of its
            // line, so the captured text carries the first line's indentation. Without this, a
            // selection that begins at the first non-whitespace character (the common case when
            // selecting a method/block) yields an empty base indent: the reindenter then rebuilds
            // every continuation line one level too shallow, while the first line still looks
            // correct because EditAsync inserts it at the caret column.
            var docText  = view.Document.Text.CopyToString();
            var selStart = sel.Start.Offset;
            var selEnd   = sel.End.Offset;

            var ls        = selStart > 0 ? docText.LastIndexOf('\n', selStart - 1) : -1;
            var lineStart = ls < 0 ? 0 : ls + 1;
            if (IsAllWhitespace(docText, lineStart, selStart))
                selStart = lineStart;

            originalCode = docText[selStart..selEnd];
            editRange    = new TextRange(new TextPosition(view.Document, selStart), sel.End);
        }
        else
        {
            var docText  = view.Document.Text.CopyToString();
            var caretOff = sel.InsertionPosition.Offset;

            var ls        = caretOff > 0 ? docText.LastIndexOf('\n', caretOff - 1) : -1;
            var lineStart = ls < 0 ? 0 : ls + 1;

            var le      = docText.IndexOf('\n', caretOff);
            var rawEnd  = le < 0 ? docText.Length : le;
            var lineEnd = rawEnd > lineStart && docText[rawEnd - 1] == '\r' ? rawEnd - 1 : rawEnd;

            originalCode = docText[lineStart..lineEnd];
            editRange    = new TextRange(
                new TextPosition(view.Document, lineStart),
                new TextPosition(view.Document, lineEnd));
        }

        if (string.IsNullOrWhiteSpace(originalCode)) return;

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
            result.TextContent, originalCode, view.Document.Text.CopyToString(),
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
        try
        {
            await Extensibility.Editor().EditAsync(
                batch =>
                {
                    var doc = view.Document.AsEditable(batch);
                    doc.Replace(editRange, editedCode);
                },
                ct);
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

    /// <summary>True if <c>text[start..end]</c> contains only whitespace (or is empty).</summary>
    private static bool IsAllWhitespace(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
            if (!char.IsWhiteSpace(text[i])) return false;
        return true;
    }

    private string ResolveModel() => ModelRouter.Resolve(_config, ModelRole.InlineEdit);

    private static List<ChatMessageDto> BuildMessages(string originalCode, string instruction)
    {
        const string System =
            "You are an expert code editor. The user provides a code block and an instruction. " +
            "Reply with ONLY the edited code — no explanation, no markdown fences, no ```.\n" +
            "CRITICAL: reproduce EXACTLY the same leading whitespace on EVERY line as the original. " +
            "ALL lines — including opening and closing braces — must keep their original indentation level. " +
            "Do NOT reset any line to column 0 if it was indented in the original. " +
            "Do not add or remove blank lines at the start or end.";

        var user = $"Code to edit:\n{originalCode}\n\nInstruction: {instruction}";

        return
        [
            new ChatMessageDto("system", System),
            new ChatMessageDto("user",   user),
        ];
    }

}
