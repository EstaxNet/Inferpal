using System.Text;
using Inferpal.Localization;
using Inferpal.Models;

namespace Inferpal.Services.Agent;

/// <summary>What the pre-send context check decided to do with the durable history.</summary>
internal enum CompactionAction
{
    /// <summary>Under budget (or nothing removable) — leave the history untouched.</summary>
    None,
    /// <summary>Drop the old turns outright (compaction disabled, or used as the safety fallback).</summary>
    Truncate,
    /// <summary>Replace the old turns with an LLM-written summary.</summary>
    Compact,
}

/// <summary>
/// The range of history messages to remove, computed once and shared by every outcome:
/// <c>Start</c>/<c>Count</c> bound the removable slice (system[0] and the optional
/// KV-cache anchor messages excluded), <c>KvAnchor</c> is the number of anchor messages
/// kept verbatim after system[0], and <c>KeepTurns</c> is the number of trailing user
/// turns preserved (both echoed in the chat notices).
/// </summary>
internal sealed record CompactionPlan(
    CompactionAction Action,
    int Start,
    int Count,
    int KvAnchor,
    int KeepTurns)
{
    public static readonly CompactionPlan None = new(CompactionAction.None, 0, 0, 0, 0);
}

/// <summary>
/// Pure decision/transformation logic for the pre-send context-window check extracted
/// from the tool-window VM (<c>CompactOrTruncateAsync</c>): the 80%-of-budget trigger,
/// the keep-turns / KV-cache-anchor range computation, the summarize-request build, and
/// the two history rewrites. The VM keeps the LLM call, its timeout fuse, and the chat
/// notices.
/// </summary>
internal static class HistoryCompaction
{
    /// <summary>
    /// Decides whether the history must shrink before the next send. Triggers when the
    /// last prompt used more than 80% of <paramref name="contextWindowSize"/>; keeps the
    /// last <paramref name="keepTurnsConfig"/> user turns (minimum 1) plus, when strictly
    /// more than <paramref name="kvAnchorMessages"/> old messages would go, the first
    /// <paramref name="kvAnchorMessages"/> messages verbatim so Ollama can reuse its KV
    /// cache for the prefix tokens across requests.
    /// </summary>
    public static CompactionPlan Decide(
        IReadOnlyList<ChatMessageDto> history,
        int contextWindowSize,
        int lastPromptTokens,
        int keepTurnsConfig,
        int kvAnchorMessages,
        bool compactionEnabled)
    {
        if (contextWindowSize <= 0 || lastPromptTokens == 0) return CompactionPlan.None;
        if (lastPromptTokens <= contextWindowSize * 8 / 10)  return CompactionPlan.None;

        var keepTurns = Math.Max(1, keepTurnsConfig);

        var userIndices = history
            .Select((m, i) => (m, i))
            .Skip(1)                       // system[0] never counts as a turn
            .Where(x => x.m.Role == "user")
            .Select(x => x.i)
            .ToList();

        if (userIndices.Count <= keepTurns) return CompactionPlan.None;

        var keepFromIdx = userIndices[userIndices.Count - keepTurns];
        var removed     = keepFromIdx - 1;

        var kvAnchor = (kvAnchorMessages > 0 && removed > kvAnchorMessages)
            ? kvAnchorMessages
            : 0;
        var start = 1 + kvAnchor;
        var count = removed - kvAnchor;

        var action = (!compactionEnabled || count == 0)
            ? CompactionAction.Truncate
            : CompactionAction.Compact;
        return new CompactionPlan(action, start, count, kvAnchor, keepTurns);
    }

    /// <summary>The slice of messages the plan removes — input for the summary transcript.</summary>
    public static List<ChatMessageDto> SliceToCompact(
        IReadOnlyList<ChatMessageDto> history, CompactionPlan plan) =>
        history.Skip(plan.Start).Take(plan.Count).ToList();

    /// <summary>
    /// The two-message request that asks the model for a summary: the original system
    /// prompt plus the labelled transcript ("User:"/"Assistant:"/"Tool:", empty messages
    /// skipped) wrapped in the localized summarize instruction.
    /// </summary>
    public static List<ChatMessageDto> BuildSummarizeRequest(
        IReadOnlyList<ChatMessageDto> history, IEnumerable<ChatMessageDto> toCompact)
    {
        var sb = new StringBuilder();
        foreach (var msg in toCompact)
        {
            if (string.IsNullOrEmpty(msg.Content)) continue;
            var label = msg.Role switch
            {
                "user"      => "User",
                "assistant" => "Assistant",
                "tool"      => "Tool",
                _           => msg.Role
            };
            sb.AppendLine($"{label}: {msg.Content}");
            sb.AppendLine();
        }
        return
        [
            history[0],
            new("user", Strings.CompactionSummarizePrompt(sb.ToString()))
        ];
    }

    /// <summary>Drops the planned range without replacement (hard truncation / safety fallback).</summary>
    public static void ApplyTruncation(List<ChatMessageDto> history, CompactionPlan plan) =>
        history.RemoveRange(plan.Start, plan.Count);

    /// <summary>
    /// Replaces the planned range with a "[Context Summary]" user/assistant pair, inserted
    /// right after the KV-cache anchors (or after system[0] when anchoring is off).
    /// </summary>
    public static void ApplySummary(List<ChatMessageDto> history, CompactionPlan plan, string summary)
    {
        history.RemoveRange(plan.Start, plan.Count);
        history.Insert(plan.Start, new ChatMessageDto("assistant", summary));
        history.Insert(plan.Start, new ChatMessageDto("user", "[Context Summary]"));
    }
}
