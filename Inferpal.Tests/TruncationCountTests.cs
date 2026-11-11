using Inferpal.Config;
using Inferpal.Models;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What the model reads in place of the turns dropped without a summary: how many messages are gone. A range that holds an
/// earlier marker stands for every message that marker stood for — counted as one, the model reads "5 dropped" for 41 and
/// takes what was said there for never said.
/// </summary>
public sealed class TruncationCountTests
{
    private static List<ChatMessageDto> Conversation(int turns)
    {
        var history = new List<ChatMessageDto> { new("system", "sys") };
        AddTurns(history, 0, turns);
        return history;
    }

    private static void AddTurns(List<ChatMessageDto> history, int from, int turns)
    {
        for (var i = from; i < from + turns; i++)
        {
            history.Add(new ChatMessageDto("user", $"question {i}"));
            history.Add(new ChatMessageDto("assistant", $"answer {i}"));
        }
    }

    private static CompactionPlan Plan(List<ChatMessageDto> history, bool summarise = false)
    {
        var plan = HistoryCompaction.Decide(history, 1_000, 100_000, keepTurnsConfig: 2, kvAnchorMessages: 0,
                                            compactionEnabled: summarise);
        Assert.NotEqual(CompactionAction.None, plan.Action);   // witness: it acts
        return plan;
    }

    [Fact]
    public void ASecondTruncation_CountsWhatTheFirstOneDropped()
    {
        var history = Conversation(20);
        var first = Plan(history);
        var held = HistoryCompaction.Apply(history, first, summary: null, held: null);
        // Reference arm: a range with no head in it counts its own messages, as it always did.
        Assert.Equal(HistoryCompaction.TruncationMarker(first.Count), history[1].Content);

        AddTurns(history, 20, 3);
        var second = Plan(history);
        Assert.Equal(1, second.Start);   // the marker is in the range
        HistoryCompaction.Apply(history, second, summary: null, held);

        Assert.Equal(HistoryCompaction.TruncationMarker(first.Count + second.Count - 1),
                     Assert.Single(history, m => m.IsScaffolding).Content);
    }

    [Fact]
    public void ATruncatedSummary_SaysThePartBeforeItWentToo()
    {
        var history = Conversation(20);
        var first = Plan(history, summarise: true);
        var held = HistoryCompaction.Apply(history, first, "SUMMARY", held: null);

        AddTurns(history, 20, 3);
        var second = Plan(history);
        HistoryCompaction.Apply(history, second, summary: null, held);

        // The summary's two messages are not two of the conversation's: what they stood for is beyond any count.
        Assert.Equal(HistoryCompaction.TruncationMarker(second.Count - 2, earlierPart: true),
                     Assert.Single(history, m => m.IsScaffolding).Content);
        Assert.DoesNotContain(history, m => m.Content == "SUMMARY");
    }

    [Fact]
    public void AMarkerThatSaysThePartBeforeIt_PassesItOn()
    {
        var history = Conversation(20);
        var held = HistoryCompaction.Apply(history, Plan(history, summarise: true), "SUMMARY", held: null);
        AddTurns(history, 20, 3);
        var second = Plan(history);
        held = HistoryCompaction.Apply(history, second, summary: null, held);
        AddTurns(history, 23, 3);
        var third = Plan(history);
        HistoryCompaction.Apply(history, third, summary: null, held);

        Assert.Equal(HistoryCompaction.TruncationMarker(second.Count - 2 + third.Count - 1, earlierPart: true),
                     Assert.Single(history, m => m.IsScaffolding).Content);
    }

    [Fact]
    public void AHeadThisCodeDoesNotRead_IsNeverCountedAsOneMessage()
    {
        var history = Conversation(6);
        history.Insert(1, new ChatMessageDto("user", "[Context Note] something an older version wrote") { IsScaffolding = true });
        var plan = new CompactionPlan(CompactionAction.Truncate, 1, 5, 0, 2);

        Assert.Equal((4, true), HistoryCompaction.Covered(history, plan));
    }

    [Fact]
    public void ASummaryOfTurnsAfterAMarker_KeepsSayingWhatWasDroppedBefore()
    {
        var history = Conversation(20);
        var first = Plan(history);
        HistoryCompaction.Apply(history, first, summary: null, held: null);
        AddTurns(history, 20, 3);
        var second = Plan(history, summarise: true);

        Assert.Equal((first.Count, false), HistoryCompaction.AlreadyDropped(history, second));
        Assert.Contains($"{first.Count} earlier message(s)", HistoryCompaction.DroppedBeforeSummaryMarker(first.Count, false),
                        StringComparison.Ordinal);
        // Reference arm: turns with no marker among them had nothing dropped before them.
        Assert.Null(HistoryCompaction.AlreadyDropped(Conversation(20), Plan(Conversation(20), summarise: true)));
    }

    private static InferpalConfig Summarising() => new()
    {
        ContextWindowSize        = 1000,
        ContextWindowKeepTurns   = 2,
        CompactionEnabled        = true,
        KvCacheAnchorMessages    = 0,
        CompactionTimeoutSeconds = 10,
    };

    private static Task<ContextDecision> CompactAsync(List<ChatMessageDto> history, string summary) =>
        ContextManager.PrepareAsync(history, Summarising(), new FakeInferenceProvider { ChatResult = new ChatTurnResult(summary, null, 0, 0) },
                                    lastPromptTokens: 100_000, onStep: null, ct: CancellationToken.None);

    /// <summary>The summary the conversation keeps carries what was dropped before its turns — and says it again when it is
    /// summarised in turn: the summarizer reads the note as one more line, and may leave it out.</summary>
    [Fact]
    public async Task TheSummaryKept_SaysWhatWasDroppedBeforeIt_AndSoDoesTheNextOne()
    {
        var history = Conversation(20);
        var first = Plan(history);
        var held = HistoryCompaction.Apply(history, first, summary: null, held: null);
        AddTurns(history, 20, 3);

        var decision = await CompactAsync(history, "FIRST SUMMARY");
        Assert.Equal(ContextOutcome.Compacted, decision.Outcome);
        Assert.Equal("FIRST SUMMARY" + HistoryCompaction.DroppedBeforeSummaryMarker(first.Count, false), decision.Summary);
        Assert.Equal("FIRST SUMMARY", decision.ShownSummary);   // the person reads the summary, not the model's note
        held = HistoryCompaction.Apply(history, decision.Plan, decision.Summary, held);

        AddTurns(history, 23, 3);
        var next = await CompactAsync(history, "SECOND SUMMARY");
        Assert.Equal("SECOND SUMMARY" + HistoryCompaction.DroppedBeforeSummaryMarker(first.Count, false), next.Summary);
        // Reference arm: turns with nothing dropped before them get no note.
        Assert.Equal("ONLY SUMMARY", (await CompactAsync(Conversation(20), "ONLY SUMMARY")).Summary);
    }
}
