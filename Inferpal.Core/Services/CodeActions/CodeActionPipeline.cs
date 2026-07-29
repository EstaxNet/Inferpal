using Inferpal.Models;

namespace Inferpal.Services.CodeActions;

/// <summary>Outcome of a portable in-place code action run (no editor applied anything yet).</summary>
internal enum CodeActionOutcome
{
    /// <summary>The model produced a rewrite; <see cref="CodeActionRun.EditedCode"/> replaces
    /// the [<see cref="CodeActionRun.Start"/>, <see cref="CodeActionRun.End"/>) range.</summary>
    Edited,
    /// <summary>The model judged the action a no-op (already good) — nothing to change.</summary>
    NoChangeNeeded,
    /// <summary>The edit could not be produced (no code, model/network error, empty reply).</summary>
    Failed,
}

/// <summary>Result of <see cref="CodeActionPipeline.RunAsync"/>. <see cref="NewDocText"/> is the
/// whole document after the rewrite — what front-ends diff against the original for the preview.
/// <see cref="FailureDetail"/> carries the underlying error message on <see cref="CodeActionOutcome.Failed"/>
/// (network/model exception), so front-ends can show the cause instead of a generic verdict.</summary>
internal sealed record CodeActionRun(
    CodeActionOutcome Outcome,
    string? EditedCode = null,
    int Start = 0,
    int End = 0,
    bool HasSelection = false,
    string? NewDocText = null,
    string? FailureDetail = null);

/// <summary>
/// Editor-agnostic pipeline of the in-place code actions (Refactor / Fix / Add-docs):
/// resolve the target from the selection → model with tools off → strip fences → no-change
/// sentinel → reindent (selection only). Shared by the VS adapter (<c>InPlaceCodeEdit</c>)
/// and the host's headless `codeAction/run` — applying the result stays editor-side.
/// Never throws on model failure (→ <see cref="CodeActionOutcome.Failed"/>); only
/// cancellation propagates.
/// </summary>
internal static class CodeActionPipeline
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
    /// Runs the model step of an in-place action and returns the rewrite without applying it.
    /// </summary>
    public static async Task<CodeActionRun> RunAsync(
        IInferenceProvider client,
        string             model,
        string             systemPrompt,
        string             instruction,
        string             docText,
        int                selStart,
        int                selEnd,
        bool               selectionEmpty,
        CancellationToken  ct)
    {
        var (originalCode, start, end, hasSelection) =
            ResolveTarget(docText, selStart, selEnd, selectionEmpty);

        if (string.IsNullOrWhiteSpace(originalCode))
            return new CodeActionRun(CodeActionOutcome.Failed);

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
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("CodeActionPipeline.RunAsync", ex);
            return new CodeActionRun(CodeActionOutcome.Failed, FailureDetail: ex.Message);
        }

        var cleaned = InlineEditResponse.Clean(result.TextContent);

        // The model signalled the action would bring nothing — leave the document untouched.
        if (CodeActionSentinel.IsNoChange(cleaned))
            return new CodeActionRun(CodeActionOutcome.NoChangeNeeded);

        // Reindent re-anchors a snippet to its original base indent — meaningful only for a
        // selection. A whole-file rewrite is emitted at column 0 by the model and applied as-is
        // (reindenting it would reformat the whole file).
        var editedCode = hasSelection
            ? InlineEditReindenter.Reindent(originalCode, cleaned)
            : cleaned;
        if (string.IsNullOrWhiteSpace(editedCode))
            return new CodeActionRun(CodeActionOutcome.Failed);

        // Small models often skip the sentinel and echo the code unchanged instead: applying it
        // would be an invisible no-op edit ("nothing happened"). Detect the identity here so every
        // front-end reports "nothing to change" like a sentinel reply.
        if (editedCode == originalCode)
            return new CodeActionRun(CodeActionOutcome.NoChangeNeeded);

        return new CodeActionRun(
            CodeActionOutcome.Edited, editedCode, start, end, hasSelection,
            NewDocText: docText[..start] + editedCode + docText[end..]);
    }

    /// <summary>True if <c>text[start..end]</c> contains only whitespace (or is empty).</summary>
    private static bool IsAllWhitespace(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
            if (!char.IsWhiteSpace(text[i])) return false;
        return true;
    }
}
