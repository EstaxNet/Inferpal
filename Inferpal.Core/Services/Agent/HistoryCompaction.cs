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
/// The summarizing request, and how many messages of the slice did not fit in it — the oldest ones, which the summary
/// then cannot cover.
/// </summary>
internal sealed record SummarizeRequest(List<ChatMessageDto> Messages, int Omitted);

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
    /// <remarks>
    /// ⚠ The kept part is sized, not only counted: the kept turns and the anchor messages stay verbatim, and with long
    /// questions (a pasted file, an attachment) four of them plus the anchors were the whole window before the tool
    /// definitions — compaction ran and the request was still refused, every turn. The system prompt, the anchors, the
    /// kept turns and <paramref name="toolTokens"/> must fit in 60 % of the window (the rest is for the summary and the
    /// answer): the anchors go first — a cache optimisation — then the oldest kept turns, never below one. Ordinary turns
    /// fit, and keep the configured number.
    /// </remarks>
    public static CompactionPlan Decide(
        IReadOnlyList<ChatMessageDto> history,
        int contextWindowSize,
        int lastPromptTokens,
        int keepTurnsConfig,
        int kvAnchorMessages,
        bool compactionEnabled,
        int toolTokens = 0)
    {
        if (contextWindowSize <= 0 || lastPromptTokens == 0) return CompactionPlan.None;
        if (lastPromptTokens <= contextWindowSize * 8 / 10)  return CompactionPlan.None;

        var userIndices = history
            .Select((m, i) => (m, i))
            .Skip(1)                       // system[0] never counts as a turn
            .Where(x => x.m.Role == "user" && !x.m.IsScaffolding)   // the loop's own prompts are not turns
            .Select(x => x.i)
            .ToList();

        var room = contextWindowSize * 6 / 10 - toolTokens - AgentOrchestrator.EstimateTokens(history.Take(1));
        int Tail(int turns) => AgentOrchestrator.EstimateTokens(history.Skip(userIndices[userIndices.Count - turns]));

        var keepTurns = Math.Min(Math.Max(1, keepTurnsConfig), Math.Max(1, userIndices.Count));
        while (keepTurns > 1 && Tail(keepTurns) > room) keepTurns--;

        if (userIndices.Count <= keepTurns) return CompactionPlan.None;

        var keepFromIdx = userIndices[userIndices.Count - keepTurns];
        var removed     = keepFromIdx - 1;

        var kvAnchor = (kvAnchorMessages > 0 && removed > kvAnchorMessages
                        && Tail(keepTurns) + AgentOrchestrator.EstimateTokens(history.Skip(1).Take(kvAnchorMessages)) <= room)
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
    /// <param name="windowTokens">The summarizer's window.</param>
    /// <param name="scale">How much the server counts above four characters per token (<c>AgentOrchestrator.ServerScale</c>):
    /// Japanese or Chinese text, about two characters per token, fills the whole window at two characters per token.</param>
    public static int SummaryInputBudgetChars(int windowTokens, double scale = 1.0) =>
        windowTokens <= 0 ? int.MaxValue : (int)Math.Min(int.MaxValue, 2.0 * windowTokens / Math.Max(1.0, scale));

    /// <summary>
    /// The reading speed the summarizing call's fuse allows for when the summarizer's own is not measured yet
    /// (<see cref="PrefillRates"/>): slower than a 27B model reads on a slow backend, so the smaller cards are covered too.
    /// </summary>
    internal const int FuseReadTokensPerSecond = 100;

    /// <summary>
    /// How long a summarizing call may run before the safety fuse falls back to hard truncation: the configured seconds
    /// (<c>compactionTimeoutSeconds</c>, 10 at least) for WRITING, plus the time to READ the request — twice its measured
    /// cold read (<paramref name="coldRead"/>), as the client waits for a first token, or a slow reader's when not measured.
    /// </summary>
    /// <remarks>
    /// ⚠ The fuse guards against a model that never answers, not against a slow one. A fixed one fired on reading alone:
    /// a model spends its first seconds reading the whole slice before it writes a word, and the slice is largest when a
    /// conversation outgrows its window — so a summary that would have kept the facts became a truncation that kept none.
    /// Both summarizing calls (the conversation's and an agent run's) read it once their request is built. The reading
    /// part allows the measured read twice over, as the client's first-token wait does (<see cref="PrefillDeadline"/>),
    /// and never less than a slow reader's time: a fit made of warm reads cannot make the fuse shorter than it was.
    /// ⚠ The fuse can still fire before the client's own wait — a floor longer than the fuse — and then names the
    /// writing time: the fuse bounds how long a compaction holds the question, and its cancellation teaches nothing.
    /// </remarks>
    public static TimeSpan SummaryTimeout(int configuredSeconds, SummarizeRequest request, TimeSpan? coldRead = null) =>
        TimeSpan.FromSeconds(Math.Max(10, configuredSeconds)
                             + Math.Max(PrefillDeadline.Margin * (coldRead?.TotalSeconds ?? 0),
                                        AgentOrchestrator.EstimateTokens(request.Messages) / FuseReadTokensPerSecond));

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
        history.InsertRange(plan.Start, SummaryPair(summary));
    }

    /// <summary>The user message a summary follows: the model knows what it reads next is a condensed past. Structural,
    /// not localized.</summary>
    public const string SummaryHeading = "[Context Summary]";

    /// <summary>The two messages that stand for the summarised turns in a history.</summary>
    public static ChatMessageDto[] SummaryPair(string summary) =>
        [new("user", SummaryHeading) { IsScaffolding = true }, new("assistant", summary)];

    /// <summary>
    /// Rewrites <paramref name="history"/> by <paramref name="plan"/> — with <paramref name="summary"/>, or dropping the
    /// turns behind a marker when it is <c>null</c> — and returns what the history holds then in place of the
    /// conversation's first turns, <paramref name="held"/> being what it held: what a session file keeps of it
    /// (<see cref="HistorySummary"/>).
    /// </summary>
    /// <returns><c>null</c> when nothing would place it in the saved conversation, which a restore then rebuilds whole and
    /// the next question measures again: <paramref name="held"/> no longer where the history held it.</returns>
    public static HistorySummary? Apply(List<ChatMessageDto> history, CompactionPlan plan, string? summary, HistorySummary? held)
    {
        // Counted before the rewrite: it takes away the questions the head then stands for.
        var questions = QuestionsReplaced(held, history, plan);
        if (summary is null) ApplyTruncation(history, plan);
        else ApplySummary(history, plan, summary);
        return questions is { } q
            ? new HistorySummary(summary ?? history[plan.Start].Content ?? string.Empty, plan.Start - 1, q, Dropped: summary is null)
            : null;
    }

    /// <summary>The messages that stand for the conversation's first turns after its anchors, as the history holds them.</summary>
    public static ChatMessageDto[] Head(HistorySummary held) =>
        held.Dropped ? [new("user", held.Text) { IsScaffolding = true }] : SummaryPair(held.Text);

    /// <summary>The conversation's questions the history holds no longer once <paramref name="plan"/> is applied — anchors
    /// included; <c>null</c> when they cannot be counted.</summary>
    private static int? QuestionsReplaced(HistorySummary? held, IReadOnlyList<ChatMessageDto> history, CompactionPlan plan)
    {
        var end       = Math.Min(plan.Start + plan.Count, history.Count);
        var from      = 1;
        var questions = 0;
        if (held is not null)
        {
            from = 1 + held.Anchors;
            var head = Head(held);
            // The head it holds is in what this plan replaces: a plan that keeps it as an anchor would stack two of them.
            if (plan.Start > from || from + head.Length > end
                || !history.Skip(from).Take(head.Length).Select(m => (m.Role, m.Content, m.IsScaffolding))
                           .SequenceEqual(head.Select(m => (m.Role, m.Content, m.IsScaffolding)))) return null;
            from     += head.Length;
            questions = held.Questions;   // its anchors' questions included, whether they stay anchors or not
        }
        for (var i = from; i < end; i++)
        {
            if (history[i].IsScaffolding) return null;   // a head the conversation does not hold
            if (history[i].Role == "user") questions++;
        }
        return questions;
    }
}

/// <summary>
/// What a model's history holds in place of a conversation's first turns once they were compacted: its first
/// <paramref name="Anchors"/> messages kept as they were, then the summary — or the marker of the turns dropped —,
/// standing for the conversation's first <paramref name="Questions"/> questions (the anchors' included). A session file
/// keeps it, so the history a restore rebuilds is the one the model had (<see cref="Persistence.SessionManager"/>).
/// </summary>
/// <remarks>⚠ Without it, a long conversation reopens whole, over the compaction trigger: its first question waits for a
/// new summary of everything, written from a request whose budget no longer reaches the conversation's start — poorer
/// than the one it replaces, and said so.</remarks>
/// <param name="Text">The summary as the model reads it, its notes on what it lacks included; the marker when
/// <paramref name="Dropped"/>.</param>
/// <param name="Dropped">The turns were dropped, not summarised (<see cref="HistoryCompaction.TruncationMarker"/>).</param>
internal sealed record HistorySummary(
    [property: System.Text.Json.Serialization.JsonPropertyName("text")]      string Text,
    [property: System.Text.Json.Serialization.JsonPropertyName("anchors")]   int Anchors,
    [property: System.Text.Json.Serialization.JsonPropertyName("questions")] int Questions,
    [property: System.Text.Json.Serialization.JsonPropertyName("dropped")]   bool Dropped = false);

/// <summary>
/// The safety fuse of a summarizing call: <see cref="HistoryCompaction.SummaryTimeout"/> until the first streamed
/// output, then the configured seconds <b>without any</b> — every chunk, reasoning included, rearms it.
/// </summary>
/// <remarks>
/// ⚠ A reasoning model thinks before it writes a word: on a 27B model, 50 to 129 s of reasoning before the first word of
/// a summary, streamed all along. A fuse counted from the start fires during that reasoning, and the summary that would
/// have kept the facts becomes a truncation that keeps none — for a model that was answering. The fuse is for a model
/// that stops producing; one that keeps repeating itself is stopped by the clients' loop detectors instead.
/// One fuse for both summarizing calls (the conversation's and an agent run's), so neither counts differently.
/// ⚠ It is also what says the wait: a model that reasons for minutes before it writes the summary sends chunks, so the
/// prefill wait has given its place back, and nothing else sees them — without it the step line stays the same for the
/// whole summary.
/// </remarks>
/// <param name="cts">The summarizing call's own source, disposed when the call returns.</param>
/// <param name="configuredSeconds">The configured writing time, <c>compactionTimeoutSeconds</c>.</param>
/// <param name="onStep">Where the call says its step: while chunks come, the step again with the time and the tokens so
/// far, at the first chunk and then every <see cref="PrefillWait.Beat"/>; <c>null</c> says nothing.</param>
/// <param name="clock">The time since "Compacting context…" was said, which the line shows; the fuse's own stopwatch
/// when <c>null</c>.</param>
internal sealed class SummaryFuse(CancellationTokenSource cts, int configuredSeconds, Action<string>? onStep = null,
                                  Func<TimeSpan>? clock = null)
{
    private readonly TimeSpan _idle = TimeSpan.FromSeconds(Math.Max(10, configuredSeconds));
    private readonly System.Diagnostics.Stopwatch _watch = System.Diagnostics.Stopwatch.StartNew();
    private readonly object _gate = new();
    private long _chars;
    private TimeSpan? _nextSay;   // null until the first chunk, which is said at once

    /// <summary>Starts the countdown before the first output: the configured seconds plus the time to read
    /// <paramref name="request"/>, from the summarizer's measured cold read (<paramref name="coldRead"/>) when there is one.</summary>
    public void Arm(SummarizeRequest request, TimeSpan? coldRead = null) =>
        cts.CancelAfter(HistoryCompaction.SummaryTimeout(configuredSeconds, request, coldRead));

    /// <summary>A streamed chunk — answer or reasoning: the model is producing, so the countdown restarts, and the step
    /// says how far the summary has come.</summary>
    /// <remarks>⚠ The countdown first: a chunk after the call returned finds its source disposed and says nothing, or a
    /// late step would bring back a status line the turn has already removed.</remarks>
    public void Progress(string chunk)
    {
        try { cts.CancelAfter(_idle); }
        catch (ObjectDisposedException) { return; }   // a late chunk after the call returned: nothing left to guard
        if (onStep is null) return;

        string line;
        lock (_gate)
        {
            _chars += chunk.Length;
            var now = clock?.Invoke() ?? _watch.Elapsed;
            if (_nextSay is { } next && now < next) return;
            _nextSay = now + PrefillWait.Beat;
            // The product's own estimate, four characters to a token, reasoning included — rounded up, so a first chunk
            // of two characters never reads "about 0 tokens", as if nothing had come.
            line = Strings.StatusCompactingProgress(RunSummary.Duration(now), (int)Math.Min(int.MaxValue, (_chars + 3) / 4));
        }
        onStep(line);
    }
}
