using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What the model reads in place of the turns dropped without a summary: how many messages are gone. A range that holds an
/// earlier marker stands for every message that marker stood for — counted as one, the model reads "5 dropped" for 41 and
/// takes what was said there for never said.
/// </summary>
[Collection(CultureSerialCollection.Name)]   // compares localized sentences
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

    /// <summary>The person's notice counts the conversation's messages a second truncation drops — the marker the range
    /// holds is no message of theirs.</summary>
    [Fact]
    public async Task ASecondTruncationsNotice_CountsTheConversationsMessages()
    {
        var history = Conversation(20);
        HistoryCompaction.Apply(history, Plan(history), summary: null, held: null);
        AddTurns(history, 20, 3);
        var config = Summarising();
        config.CompactionEnabled = false;

        var decision = await ContextManager.PrepareAsync(history, config, new FakeInferenceProvider(), lastPromptTokens: 100_000,
                                                         onStep: null, ct: CancellationToken.None);

        Assert.Equal(ContextOutcome.Truncated, decision.Outcome);
        Assert.Equal(1, decision.Plan.Start);   // witness: the marker is in the range
        Assert.Equal(Strings.MsgContextTruncated(decision.Plan.Count - 1, decision.Plan.KeepTurns), decision.Notice);
    }

    /// <summary>A plan that replaces only the summary held (the request over the window, every turn after it kept) removes
    /// none of the person's messages: the notice says the summary went, never "2 old messages".</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyTheSummaryHeld_Replaced_IsSaidAsTheSummary(bool summarise)
    {
        var history = new List<ChatMessageDto> { new("system", "sys") };
        history.AddRange(HistoryCompaction.SummaryPair("EARLIER SUMMARY"));
        AddTurns(history, 0, 1);
        var config = Summarising();
        config.CompactionEnabled = summarise;
        config.ContextWindowKeepTurns = 5;

        var decision = await CompactAsync(history, "AGAIN", config);

        Assert.Equal((1, 2), (decision.Plan.Start, decision.Plan.Count));   // witness: the summary pair, alone
        Assert.StartsWith(summarise ? Strings.MsgContextCompactedSummaryOnly(decision.Plan.KeepTurns)
                                    : Strings.MsgContextTruncatedSummaryOnly(decision.Plan.KeepTurns), decision.Notice);
    }

    [Fact]
    public void WhatTheSummarisingRequestLeftOut_IsTheConversationsMessages_AndTheSummaryHeld()
    {
        var history = Conversation(10);
        history.InsertRange(1, HistoryCompaction.SummaryPair("S"));
        var plan = new CompactionPlan(CompactionAction.Compact, 1, 16, 0, 2);

        Assert.Equal((3, true), HistoryCompaction.Omitted(history, plan, 5));    // heading, summary, three messages
        // The heading of a summary whose text was sent goes with it: nothing of the conversation left out.
        Assert.Equal((0, false), HistoryCompaction.Omitted(history, plan, 1));
        // An empty summary text is skipped by the request, never sent: its heading left out is the summary left out.
        var blank = Conversation(10);
        blank.InsertRange(1, HistoryCompaction.SummaryPair(""));
        Assert.Equal((0, true), HistoryCompaction.Omitted(blank, plan, 1));

        var marked = Conversation(10);
        marked.Insert(1, new ChatMessageDto("user", HistoryCompaction.TruncationMarker(40)) { IsScaffolding = true });
        // A marker left out is not counted here (the note of what was dropped before says it), and alone it is no
        // partial summary.
        Assert.Equal((0, false), HistoryCompaction.Omitted(marked, plan with { Count = 15 }, 1));
        Assert.Equal((2, false), HistoryCompaction.Omitted(marked, plan with { Count = 15 }, 3));
    }

    [Fact]
    public void ThePartialNote_SaysTheSummaryHeld_WithOrWithoutMessages()
    {
        Assert.Contains("its first 4 message(s), and the summary of the part before them, did not fit",
                        HistoryCompaction.PartialSummaryMarker(4, summary: true), StringComparison.Ordinal);
        Assert.Contains("the summary of its beginning did not fit", HistoryCompaction.PartialSummaryMarker(0, summary: true),
                        StringComparison.Ordinal);
        Assert.Contains("its first 4 message(s) did not fit", HistoryCompaction.PartialSummaryMarker(4), StringComparison.Ordinal);
    }

    private static InferpalConfig Summarising() => new()
    {
        ContextWindowSize        = 1000,
        ContextWindowKeepTurns   = 2,
        CompactionEnabled        = true,
        KvCacheAnchorMessages    = 0,
        CompactionTimeoutSeconds = 10,
    };

    private static Task<ContextDecision> CompactAsync(List<ChatMessageDto> history, string summary, InferpalConfig? config = null) =>
        ContextManager.PrepareAsync(history, config ?? Summarising(), new FakeInferenceProvider { ChatResult = new ChatTurnResult(summary, null, 0, 0) },
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
