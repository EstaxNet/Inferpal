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
/// <summary>
/// The summarizing request, and how many messages of the slice did not fit in it — the oldest ones, which the summary
/// then cannot cover.
/// </summary>
internal sealed record SummarizeRequest(List<ChatMessageDto> Messages, int Omitted);

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
            .Where(x => x.m.Role == "user" && !x.m.IsScaffolding)   // the loop's own prompts are not turns
            .Select(x => x.i)
            .ToList();

        if (userIndices.Count <= keepTurns) return CompactionPlan.None;

        var keepFromIdx = userIndices[userIndices.Count - keepTurns];
        var removed     = keepFromIdx - 1;

        var kvAnchor = (kvAnchorMessages > 0 && removed > kvAnchorMessages)
            ? kvAnchorMessages
            : 0;

        // The tail is safe by construction (it starts on a user message), but the KV-cache anchor
        // boundary is not: it can leave an anchored assistant whose tool_calls lose their answers.
        // Widening the removal costs one cached prefix message; not widening it produces a history
        // OpenAI-compatible backends reject. See ToolBlockBoundary.
        var start = ToolBlockBoundary.SnapStart(history, 1 + kvAnchor, floor: 1);
        var count = keepFromIdx - start;
        if (count <= 0) return CompactionPlan.None;

        // Report the anchor actually preserved, so the chat notice never overstates it.
        kvAnchor = start - 1;

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
    /// The characters of transcript a summarizing request may carry into a window of <paramref name="windowTokens"/>:
    /// two per token, so half the window at the usual four characters per token, and still two thirds of it for code,
    /// which runs closer to three. The rest is the instruction's and the summary's. No window known → no bound.
    /// </summary>
    public static int SummaryInputBudgetChars(int windowTokens) =>
        windowTokens <= 0 ? int.MaxValue : (int)Math.Min(int.MaxValue, 2L * windowTokens);

    /// <summary>
    /// The request that asks the model for a summary: the labelled transcript ("User:"/"Assistant:"/"Tool:", empty
    /// messages skipped) wrapped in the localized summarize instruction, within <paramref name="budgetChars"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ Bounded, because the slice to compact is largest exactly when a summary is needed most — a long conversation
    /// reopened is measured and compacted at its first question, and an agent run's slice is full of tool output. Past
    /// the summarizer's window LM Studio refuses the request (the turns are then dropped with no summary at all) and
    /// Ollama cuts its head in silence, so the "summary" covers only the end of what it claims to. What does not fit
    /// is the OLDEST part: the newest is what the kept turns continue from. The loss is counted in
    /// <see cref="SummarizeRequest.Omitted"/>, and the caller says it to both readers (<see cref="PartialSummaryMarker"/>).
    /// ⚠ No system prompt: the summarizer needs the conversation, not the pinned files, rules and memory the chat
    /// prompt carries — which can fill a quarter of the window on their own. The price is that Ollama, when the
    /// utility model is the chat model, re-reads the system prompt once on the next question.
    /// </remarks>
    public static SummarizeRequest BuildSummarizeRequest(
        IReadOnlyList<ChatMessageDto> toCompact, int budgetChars = int.MaxValue)
    {
        var (transcript, omitted) = BoundedTranscript(toCompact, budgetChars);
        return new SummarizeRequest(
            [new ChatMessageDto("user", Strings.CompactionSummarizePrompt(transcript))], omitted);
    }

    /// <summary>
    /// The labelled transcript of <paramref name="messages"/> ("User:"/"Assistant:"/"Tool:", empty messages skipped),
    /// newest first until <paramref name="budgetChars"/> is spent, and how many of the messages were left out — the
    /// oldest. One reader for every request that asks a model to summarize a conversation (compaction, session recap).
    /// </summary>
    public static (string Text, int Omitted) BoundedTranscript(IReadOnlyList<ChatMessageDto> messages, int budgetChars)
    {
        // A newest message larger than the whole budget is sent cut rather than not at all: it is the one the rest of
        // the conversation continues from. Omitted counts messages of the input, as a notice's total does — empty ones
        // included.
        var kept    = new List<string>();
        var used    = 0;
        var omitted = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var content = messages[i].Content;
            if (string.IsNullOrEmpty(content)) continue;
            var entry = $"{Label(messages[i].Role)}: {content}";
            if (used + entry.Length + 2 <= budgetChars) { kept.Insert(0, entry); used += entry.Length + 2; continue; }
            if (kept.Count == 0 && budgetChars > 0)
            {
                kept.Add(SafeTruncate.Truncate(entry, budgetChars) + "\n…(truncated)");
                omitted = i;
            }
            else omitted = i + 1;
            break;
        }

        var sb = new StringBuilder();
        // Model-facing and structural: not localized. Without it an instruction that says "the beginning of our
        // conversation" presents a middle as a beginning.
        if (omitted > 0)
            sb.AppendLine($"[The first {omitted} message(s) did not fit in this request and are not shown.]").AppendLine();
        foreach (var entry in kept) sb.AppendLine(entry).AppendLine();
        return (sb.ToString(), omitted);

        static string Label(string role) => role switch
        {
            "user"      => "User",
            "assistant" => "Assistant",
            "tool"      => "Tool",
            _           => role
        };
    }

    /// <summary>
    /// Appended to a summary written from only the newest part of the slice (<see cref="SummarizeRequest.Omitted"/>):
    /// read as whole, it makes the model deny what was said in the part it never saw. Not localized, like
    /// <see cref="TruncationMarker"/>: a structural marker in the transcript.
    /// </summary>
    public static string PartialSummaryMarker(int omitted) =>
        $"\n\n[Context Note] This summary covers only the most recent part of the earlier conversation: its first "
      + $"{omitted} message(s) did not fit in the summarizing request and are missing from it. If the user refers to "
      + "something you cannot find here, say you no longer have it rather than treating it as never said.";

    /// <summary>
    /// What the model is told in place of the turns that were dropped without a summary.
    /// </summary>
    /// <remarks>
    /// ⚠ Deliberately NOT localized, like <c>[Context Summary]</c> next door: this is a structural
    /// marker in the transcript, not prose shown to anyone. Read through this helper rather than
    /// copied at the far end — a phrase copied into an assertion matches until the day the phrase is
    /// reworded, then stops without a sign.
    /// </remarks>
    public static string TruncationMarker(int dropped) =>
        $"[Context Note] {dropped} earlier message(s) of this conversation were dropped to fit the "
      + "context window. That part is gone from your context: if the user refers to something said "
      + "there, say you no longer have it rather than treating it as never said.";

    /// <summary>
    /// Appended to a summary that stopped at the model's length limit: the summary is kept — better than dropping the
    /// turns — but read as whole it makes the model deny what was said past the cut. Not localized, like
    /// <see cref="TruncationMarker"/>: a structural marker in the transcript.
    /// </summary>
    public const string CutSummaryMarker =
        "\n\n[Context Note] This summary stopped at the model's length limit: part of the earlier conversation is "
      + "missing from it. If the user refers to something you cannot find here, say you no longer have it rather "
      + "than treating it as never said.";

    /// <summary>
    /// Drops the planned range, leaving the model a marker in its place (hard truncation / safety
    /// fallback).
    /// </summary>
    /// <remarks>
    /// ⚠ The marker is the whole point. A bare <c>RemoveRange</c> leaves the model a conversation
    /// that simply starts later, which reads as "this is all of it": the user writes "as I told you
    /// earlier…" and gets told it was never mentioned. <see cref="ApplySummary"/> two lines down has
    /// always marked its own rewrite (<c>[Context Summary]</c>) — the same rule, honoured by one of
    /// its two sites. ⚠ <c>IsScaffolding</c> is not decoration: it is what keeps this <c>user</c>
    /// message out of the turn count (<see cref="Decide"/> filters on it), which
    /// <c>contextWindowKeepTurns</c>, <c>/branch</c> numbering and regeneration all read.
    /// </remarks>
    public static void ApplyTruncation(List<ChatMessageDto> history, CompactionPlan plan)
    {
        history.RemoveRange(plan.Start, plan.Count);
        history.Insert(plan.Start,
                       new ChatMessageDto("user", TruncationMarker(plan.Count)) { IsScaffolding = true });
    }

    /// <summary>
    /// Replaces the planned range with a "[Context Summary]" user/assistant pair, inserted
    /// right after the KV-cache anchors (or after system[0] when anchoring is off).
    /// </summary>
    public static void ApplySummary(List<ChatMessageDto> history, CompactionPlan plan, string summary)
    {
        history.RemoveRange(plan.Start, plan.Count);
        history.Insert(plan.Start, new ChatMessageDto("assistant", summary));
        history.Insert(plan.Start, new ChatMessageDto("user", "[Context Summary]") { IsScaffolding = true });
    }
}
