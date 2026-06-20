using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Editor;

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
        catch { return; }

        string? instruction;
        try
        {
            instruction = await dlg.InstructionTask;
        }
        catch
        {
            dlg.CloseFromThread();
            return;
        }

        if (string.IsNullOrWhiteSpace(instruction))
        {
            // User cancelled — dialog already closed itself.
            return;
        }

        // Instruction received → switch dialog to spinner mode.
        dlg.SwitchToLoading();

        // ── 4. Call Ollama ────────────────────────────────────────────────────
        var model    = ResolveModel();
        var messages = BuildMessages(originalCode, instruction);

        ChatTurnResult result;
        try
        {
            result = await _client.SendChatAsync(
                model, messages, EmptyToolRegistry.Instance, onToken: null, ct, TaskComplexity.Quick);
        }
        catch
        {
            dlg.CloseFromThread();
            return;
        }
        finally
        {
            // Always close the spinner, even on error/cancel.
            dlg.CloseFromThread();
        }

        var editedCode = InlineEditReindenter.Reindent(originalCode, InlineEditResponse.Clean(result.TextContent));
        if (string.IsNullOrWhiteSpace(editedCode)) return;

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
        catch
        {
            // EditAsync may fail if the document was modified between the initial
            // snapshot and the apply call.  Silently discard — the user can retry.
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>True if <c>text[start..end]</c> contains only whitespace (or is empty).</summary>
    private static bool IsAllWhitespace(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
            if (!char.IsWhiteSpace(text[i])) return false;
        return true;
    }

    private string ResolveModel()
    {
        if (!string.IsNullOrEmpty(_config.InlineEditModel))  return _config.InlineEditModel;
        if (!string.IsNullOrEmpty(_config.CodeActionsModel)) return _config.CodeActionsModel;
        return _config.DefaultModel;
    }

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
