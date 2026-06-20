using Inferpal.Models;
using Inferpal.Services;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;

namespace Inferpal.Commands;

/// <summary>Outcome of an in-place code edit.</summary>
internal enum InPlaceEditOutcome
{
    /// <summary>The model produced a rewrite and it was applied to the document.</summary>
    Applied,
    /// <summary>The model judged the action a no-op (already good) — nothing was changed.</summary>
    NoChangeNeeded,
    /// <summary>The edit could not be produced or applied (no code, model/edit error).</summary>
    Failed,
}

/// <summary>
/// Shared pipeline for code actions that <b>apply their result directly to the document</b>
/// (Refactor / Fix / Add-docs), used both by the editor context-menu commands
/// (<see cref="InPlaceCodeActionBase"/>) and the equivalent chat slash commands.
///
/// <para>Scope follows the selection: a non-empty selection is rewritten on its own;
/// otherwise the whole file is. Tools are disabled — the model must reply with code only.</para>
/// </summary>
internal static class InPlaceCodeEdit
{
    /// <summary>
    /// Resolves the target text and range from a document's text and the selection offsets.
    /// Pure (offset arithmetic only) so it can be unit-tested without the editor.
    /// </summary>
    /// <returns>The original code, its [start,end) offsets, and whether a selection drove it.</returns>
    public static (string Code, int Start, int End, bool HasSelection) ResolveTarget(
        string docText, int selStart, int selEnd, bool selectionEmpty)
    {
        if (!selectionEmpty && selEnd > selStart)
        {
            // Expand the start back over leading whitespace to the line start so the captured
            // snippet carries its first line's indentation — otherwise the reindenter rebuilds
            // continuation lines one level too shallow.
            var ls        = selStart > 0 ? docText.LastIndexOf('\n', selStart - 1) : -1;
            var lineStart = ls < 0 ? 0 : ls + 1;
            if (IsAllWhitespace(docText, lineStart, selStart))
                selStart = lineStart;

            return (docText[selStart..selEnd], selStart, selEnd, true);
        }

        return (docText, 0, docText.Length, false);
    }

    /// <summary>
    /// Runs the full pipeline: spinner → model (tools off) → strip fences → reindent (selection
    /// only) → replace the range via <c>EditAsync</c> (undoable with Ctrl+Z).
    /// Never throws; returns the <see cref="InPlaceEditOutcome"/>. When the model decides the code
    /// is already good (sentinel reply), nothing is applied and <see cref="InPlaceEditOutcome.NoChangeNeeded"/>
    /// is returned.
    /// </summary>
    public static async Task<InPlaceEditOutcome> RunAsync(
        VisualStudioExtensibility vs,
        ITextViewSnapshot         view,
        IInferenceProvider        client,
        string                    model,
        string                    systemPrompt,
        string                    instruction,
        CancellationToken         ct)
    {
        var docText = view.Document.Text.CopyToString();
        var sel     = view.Selection;
        var (originalCode, start, end, hasSelection) =
            ResolveTarget(docText, sel.Start.Offset, sel.End.Offset, sel.IsEmpty);

        if (string.IsNullOrWhiteSpace(originalCode)) return InPlaceEditOutcome.Failed;

        var editRange = new TextRange(
            new TextPosition(view.Document, start),
            new TextPosition(view.Document, end));

        // Spinner overlay while the model generates.
        InlineEditInputWindow dlg;
        try { dlg = await InlineEditInputWindow.CreateAndShowSpinnerAsync(); }
        catch { return InPlaceEditOutcome.Failed; }

        var messages = new List<ChatMessageDto>
        {
            new("system", systemPrompt),
            new("user",   $"{instruction}\n\n{originalCode}"),
        };

        ChatTurnResult result;
        try
        {
            result = await client.SendChatAsync(
                model, messages, EmptyToolRegistry.Instance, onToken: null, ct, TaskComplexity.Quick);
        }
        catch
        {
            dlg.CloseFromThread();
            return InPlaceEditOutcome.Failed;
        }
        finally
        {
            dlg.CloseFromThread();
        }

        var cleaned = InlineEditResponse.Clean(result.TextContent);

        // The model signalled the action would bring nothing — leave the document untouched.
        if (CodeActionSentinel.IsNoChange(cleaned)) return InPlaceEditOutcome.NoChangeNeeded;

        // Reindent re-anchors a snippet to its original base indent — meaningful only for a
        // selection. A whole-file rewrite is emitted at column 0 by the model and applied as-is
        // (reindenting it would reformat the whole file).
        var editedCode = hasSelection
            ? InlineEditReindenter.Reindent(originalCode, cleaned)
            : cleaned;
        if (string.IsNullOrWhiteSpace(editedCode)) return InPlaceEditOutcome.Failed;

        try
        {
            await vs.Editor().EditAsync(
                batch =>
                {
                    var doc = view.Document.AsEditable(batch);
                    doc.Replace(editRange, editedCode);
                },
                ct);
            return InPlaceEditOutcome.Applied;
        }
        catch
        {
            // EditAsync may fail if the document changed between snapshot and apply — user can retry.
            return InPlaceEditOutcome.Failed;
        }
    }

    /// <summary>True if <c>text[start..end]</c> contains only whitespace (or is empty).</summary>
    private static bool IsAllWhitespace(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
            if (!char.IsWhiteSpace(text[i])) return false;
        return true;
    }
}
