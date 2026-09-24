using Inferpal.Localization;
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
    /// How many line breaks the replaced text carried at its end — the <c>\r</c> of a pair is not
    /// one more, and a selection of several blank lines keeps as many.
    /// </summary>
    private static int TrailingLineBreaks(string text)
    {
        var count = 0;
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (text[i] == '\n') { count++; continue; }
            if (text[i] == '\r') continue;   // the CR of its pair
            break;
        }
        return count;
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

        var finished = Finish(result.TextContent, originalCode, docText, hasSelection,
                              model, client.ServerAddress, result.CutAtLimit);
        if (finished.Outcome != CodeActionOutcome.Edited)
            return finished;

        var editedCode = finished.EditedCode!;
        return new CodeActionRun(
            CodeActionOutcome.Edited, editedCode, start, end, hasSelection,
            NewDocText: docText[..start] + editedCode + docText[end..]);
    }

    /// <summary>
    /// Turns a model reply into the text to apply — or into the verdict that nothing should be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ This is a FUNNEL: everything that turns a model reply into an edit goes through it, and a
    /// caller doing its own <c>Reindent(Clean(reply))</c> misses four of the steps below. Two of
    /// them answer the most ordinary gesture in Visual Studio — a click in the margin plus
    /// Shift+Down selects the line WITH its ending, <c>Clean</c> trims it
    /// (<c>"    return 1;\r\n"</c> → <c>"    return 1;"</c>), the next line moves up against the
    /// edited one, and the unchanged echo now differs from the original by that single byte, so it
    /// is applied instead of being reported as "nothing to change".
    /// </para>
    /// <para>
    /// ⚠ Order is the substance here: the line breaks are restored AFTER the empty guard (which an
    /// added break would disarm) and BEFORE the identity check (which they are what makes exact).
    /// </para>
    /// </remarks>
    internal static CodeActionRun Finish(
        string? reply, string originalCode, string docText, bool reindent,
        string model, string serverAddress, bool cutAtLimit)
    {
        // ⚠ An answer that stopped at the length limit is the FIRST part of the rewrite — the window
        // fills up exactly when the whole file goes in and the whole file is expected out. Applied, it
        // replaces the code with its beginning and the rest of the file is gone; the server says so
        // (finish_reason / done_reason "length"), so nothing is applied and the cause is named.
        if (cutAtLimit)
            return new CodeActionRun(CodeActionOutcome.Failed, FailureDetail: Strings.CodeActionReplyCut);

        var cleaned = InlineEditResponse.Clean(reply ?? string.Empty);

        // The model signalled the action would bring nothing — leave the document untouched.
        if (CodeActionSentinel.IsNoChange(cleaned))
            return new CodeActionRun(CodeActionOutcome.NoChangeNeeded);

        // Reindent re-anchors a snippet to its original base indent — meaningful only for a
        // selection. A whole-file rewrite is emitted at column 0 by the model and applied as-is
        // (reindenting it would reformat the whole file).
        var editedCode = reindent
            ? InlineEditReindenter.Reindent(originalCode, cleaned)
            : cleaned;
        // ⚠ Model output (and Reindent's) is LF: a CRLF document gets its own endings back — both to
        // write consistent endings and to recognise an unchanged LF echo as "nothing to change".
        var eol = LineEndings.Dominant(docText);
        editedCode = LineEndings.ToEol(editedCode, eol);
        // An empty reply is a model-side condition (wrong model kind, aborted stream) — name it,
        // so the failure prompt carries a cause instead of the bare generic verdict.
        if (string.IsNullOrWhiteSpace(editedCode))
            return new CodeActionRun(CodeActionOutcome.Failed,
                                     FailureDetail: Strings.MsgEmptyResponseFrom(model, serverAddress));

        // ⚠ Visual Studio's most common selection — a click in the margin, Shift+Down — carries its
        // LINE ENDING, and `InlineEditResponse.Clean` does a TrimEnd: the replacement never ended
        // with a line break, so the next line moved up and stuck to the one just edited. Worse, the
        // unchanged echo no longer recognised itself — it differed from the original by that single
        // byte — so the "no change" edit was applied anyway, eating the line ending. The cleanup
        // cannot decide this: it does not see the replaced range. Restored BEFORE the identity
        // check, for the same reason, and AFTER the empty guard, which an added line break would
        // disarm.
        var breaks = TrailingLineBreaks(originalCode);
        for (var i = 0; i < breaks; i++) editedCode += eol;

        // Small models often skip the sentinel and echo the code unchanged instead: applying it
        // would be an invisible no-op edit ("nothing happened"). Detect the identity here so every
        // front-end reports "nothing to change" like a sentinel reply.
        if (editedCode == LineEndings.ToEol(originalCode, eol))
            return new CodeActionRun(CodeActionOutcome.NoChangeNeeded);

        // The range and the rebuilt document belong to the CALLER: this funnel decides the text and
        // the verdict, not where they land — "Edit with AI" replaces a range it resolved itself.
        return new CodeActionRun(CodeActionOutcome.Edited, editedCode);
    }

    /// <summary>True if <c>text[start..end]</c> contains only whitespace (or is empty).</summary>
    private static bool IsAllWhitespace(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
            if (!char.IsWhiteSpace(text[i])) return false;
        return true;
    }
}
