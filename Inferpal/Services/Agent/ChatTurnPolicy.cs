using System.Text;
using Inferpal.Localization;
using Inferpal.ToolWindow;

namespace Inferpal.Services.Agent;

/// <summary>
/// What the final-render pass of a chat turn should display, in fallback order:
/// the live-streamed bubble, the stored final response, a tool-execution summary,
/// or the absolute "empty response" fallback.
/// </summary>
internal enum FinalAnswerKind
{
    StreamedAnswer,
    FinalText,
    ToolSummary,
    EmptyFallback,
}

/// <summary>
/// Pure decision/formatting logic extracted from the tool-window VM's send pipeline
/// (<c>SendCoreAsync</c>): the empty-bubble triple guard, the final-answer fallback
/// chain, tool-bubble previews, the context-enriched history text, the multi-file
/// recap inputs, and the prompt-history rules. The VM keeps UI state, message
/// insertion, theming, and the LLM calls.
/// </summary>
internal static class ChatTurnPolicy
{
    /// <summary>
    /// Triple guard against visually empty assistant bubbles (see bug history): content
    /// is "visibly empty" when it parses to no markdown blocks, contains no printable
    /// character once &lt;think&gt; tags are stripped (whitespace runs, zero-width
    /// spaces, BOM…), or parses only to thematic-break separators ("---" renders as an
    /// invisible 1-px line). Matches <c>ChatMessageItem.ParseMarkdown</c>'s outcome:
    /// <c>!HasBlocks || !HasPrintableText(stripped) || Blocks.All(separator)</c>.
    /// </summary>
    public static bool IsVisiblyEmpty(string? content)
    {
        if (!MarkdownParser.HasPrintableText(MarkdownParser.StripThinkTags(content)))
            return true;
        var blocks = MarkdownParser.Parse(content ?? string.Empty);
        return blocks.Count == 0 || blocks.All(b => b.Type == "separator");
    }

    /// <summary>
    /// Picks what the final render pass should show. <paramref name="finalResponse"/> is
    /// the agent's stored final response (may still contain &lt;think&gt; tags).
    /// </summary>
    public static FinalAnswerKind DecideFinalAnswer(
        bool streamingBubbleVisible, string? finalResponse, int executionCount)
    {
        if (streamingBubbleVisible)            return FinalAnswerKind.StreamedAnswer;
        if (!IsVisiblyEmpty(finalResponse))    return FinalAnswerKind.FinalText;
        if (executionCount > 0)                return FinalAnswerKind.ToolSummary;
        return FinalAnswerKind.EmptyFallback;
    }

    /// <summary>"read_file, write_file ×3" — tool names grouped with a ×count when repeated.</summary>
    public static string BuildToolSummary(IEnumerable<ToolExecution> executions) =>
        string.Join(", ", executions
            .GroupBy(e => e.Name)
            .Select(g => g.Count() == 1 ? g.Key : $"{g.Key} \xd7{g.Count()}"));

    /// <summary>Truncates a tool output for its result bubble (full output stays in the history).</summary>
    public static string BuildToolPreview(string output, int maxLength = 500) =>
        output.Length > maxLength ? output[..maxLength] + Strings.MsgTruncated : output;

    /// <summary>
    /// Builds the context-enriched history message sent to the model (not shown in the
    /// chat bubble): each attachment as a labelled fenced block, then the user's text.
    /// </summary>
    public static string BuildHistoryText(string userText, IReadOnlyList<AttachmentItem> attachments)
    {
        if (attachments.Count == 0) return userText;
        var sb = new StringBuilder();
        foreach (var att in attachments)
        {
            sb.AppendLine($"[Attached: {att.Label}]");
            sb.AppendLine("```");
            sb.AppendLine(att.Content);
            sb.AppendLine("```");
            sb.AppendLine();
        }
        sb.Append(userText);
        return sb.ToString();
    }

    /// <summary>
    /// Distinct file paths written by <c>write_file</c>/<c>apply_diff</c> during the run —
    /// drives the multi-file recap bubble (shown when ≥ 2 files were modified).
    /// </summary>
    public static List<string> ModifiedFilePaths(IEnumerable<ToolExecution> executions) =>
        executions
            .Where(e => (e.Name == "write_file" || e.Name == "apply_diff")
                     && e.Diff is not null && !string.IsNullOrEmpty(e.Diff.FilePath))
            .Select(e => e.Diff!.FilePath)
            .Distinct()
            .ToList();

    /// <summary>
    /// The assistant text persisted in the durable history: the bubble actually shown to
    /// the user when there is one, the stored final response otherwise — think tags
    /// stripped in both cases. Returns an empty string when nothing is worth persisting.
    /// </summary>
    public static string ChoosePersistedAnswer(string? shownBubbleContent, string? finalResponse)
    {
        var answer = MarkdownParser.StripThinkTags(shownBubbleContent ?? finalResponse);
        return string.IsNullOrWhiteSpace(answer) ? string.Empty : answer;
    }

    /// <summary>
    /// Appends a prompt to the recall history (no duplicate at the top, oldest evicted
    /// past <paramref name="max"/>). Returns <c>true</c> when the list changed and
    /// should be saved.
    /// </summary>
    public static bool AppendPromptHistory(List<string> history, string prompt, int max)
    {
        if (history.Count > 0 && history[^1] == prompt) return false;
        history.Add(prompt);
        if (history.Count > max)
            history.RemoveAt(0);
        return true;
    }

    /// <summary>Single-line preview: capped at <paramref name="max"/> chars (ellipsis added), newlines flattened.</summary>
    public static string OneLinePreview(string text, int max)
    {
        var capped = text.Length > max ? text[..max] + "…" : text;
        return capped.Replace('\n', ' ');
    }

    /// <summary>
    /// The <c>/phistory</c> listing: entries matching <paramref name="term"/> (all when
    /// <c>null</c>), most recent first, each with its 1-based index and a ready-to-type
    /// <c>/phistory use n</c>. Returns <c>null</c> when nothing matches (the VM shows
    /// the notice).
    /// </summary>
    public static string? FormatPromptHistory(IReadOnlyList<string> history, string? term)
    {
        var matches = history
            .Select((p, i) => (Idx: i + 1, Text: p))
            .Where(x => term is null || x.Text.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Reverse()     // most recent first
            .ToList();
        if (matches.Count == 0) return null;

        var sb = new StringBuilder((term is null ? Strings.PHistoryListHeader : Strings.PHistoryListHeaderTerm(term)) + "\n\n");
        foreach (var (idx, text) in matches)
            sb.AppendLine($"**#{idx}** {OneLinePreview(text, 80)}  `/phistory use {idx}`");
        return sb.ToString().TrimEnd();
    }
}
