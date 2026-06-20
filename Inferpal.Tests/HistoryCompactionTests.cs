using System.Collections.Generic;
using System.Linq;
using Inferpal.Models;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Covers the pure pre-send context-window logic extracted from the tool-window VM
// (CompactOrTruncateAsync): the 80%-of-budget trigger, the keep-turns / KV-cache-anchor
// range computation, the summarize-request transcript, and the two history rewrites.
// The LLM call, its timeout fuse, and the chat notices stay in the VM and are not
// tested here.
public class HistoryCompactionTests
{
    // Builds "system, (user, assistant) × turns" — the shape of the durable history.
    private static List<ChatMessageDto> History(int turns)
    {
        var h = new List<ChatMessageDto> { new("system", "sys") };
        for (var i = 1; i <= turns; i++)
        {
            h.Add(new("user", $"question {i}"));
            h.Add(new("assistant", $"answer {i}"));
        }
        return h;
    }

    private static CompactionPlan Decide(
        List<ChatMessageDto> history,
        int limit              = 1000,
        int lastPromptTokens   = 900,
        int keepTurns          = 2,
        int kvAnchor           = 0,
        bool compactionEnabled = true) =>
        HistoryCompaction.Decide(history, limit, lastPromptTokens, keepTurns, kvAnchor, compactionEnabled);

    // ── Decide: the 80% trigger ────────────────────────────────────────────────

    [Theory]
    [InlineData(0,    900)] // no budget configured
    [InlineData(1000, 0)]   // first send of the session — no measurement yet
    [InlineData(1000, 800)] // exactly 80% is still within budget
    public void Decide_None_WhenUnderBudgetOrUnmeasured(int limit, int lastPromptTokens) =>
        Assert.Equal(CompactionAction.None,
            Decide(History(10), limit, lastPromptTokens).Action);

    [Fact]
    public void Decide_Triggers_JustAbove80Percent() =>
        Assert.Equal(CompactionAction.Compact,
            Decide(History(10), limit: 1000, lastPromptTokens: 801).Action);

    // ── Decide: keep-turns boundaries ──────────────────────────────────────────

    [Fact]
    public void Decide_None_WhenAllTurnsMustBeKept() =>
        Assert.Equal(CompactionAction.None, Decide(History(2), keepTurns: 2).Action);

    [Fact]
    public void Decide_KeepTurnsHasMinimumOfOne()
    {
        var plan = Decide(History(3), keepTurns: 0);
        Assert.Equal(1, plan.KeepTurns);
        // Keep the last user turn (index 5) → remove indices 1..4.
        Assert.Equal(1, plan.Start);
        Assert.Equal(4, plan.Count);
    }

    [Fact]
    public void Decide_RemovesEverythingBeforeTheKeptTurns()
    {
        // 5 turns, keep 2 → last kept user message is at index 7 → remove indices 1..6.
        var plan = Decide(History(5), keepTurns: 2);
        Assert.Equal(CompactionAction.Compact, plan.Action);
        Assert.Equal(1, plan.Start);
        Assert.Equal(6, plan.Count);
        Assert.Equal(0, plan.KvAnchor);
    }

    // ── Decide: KV-cache anchor ────────────────────────────────────────────────

    [Fact]
    public void Decide_KvAnchor_ShrinksTheRemovedRange()
    {
        // 6 removable messages, anchor 2 → keep indices 1-2 verbatim, remove 3..6.
        var plan = Decide(History(5), keepTurns: 2, kvAnchor: 2);
        Assert.Equal(2, plan.KvAnchor);
        Assert.Equal(3, plan.Start);
        Assert.Equal(4, plan.Count);
    }

    [Fact]
    public void Decide_KvAnchor_InactiveWhenNotStrictlyMoreOldMessagesThanAnchor()
    {
        // 6 removable messages, anchor 6 → "removed > anchor" is false → anchor off.
        var plan = Decide(History(5), keepTurns: 2, kvAnchor: 6);
        Assert.Equal(0, plan.KvAnchor);
        Assert.Equal(1, plan.Start);
        Assert.Equal(6, plan.Count);
    }

    // ── Decide: truncation fallback ────────────────────────────────────────────

    [Fact]
    public void Decide_Truncate_WhenCompactionDisabled() =>
        Assert.Equal(CompactionAction.Truncate,
            Decide(History(5), compactionEnabled: false).Action);

    [Fact]
    public void Decide_Truncate_StillProtectsTheKvAnchors()
    {
        var plan = Decide(History(5), compactionEnabled: false, kvAnchor: 2);
        Assert.Equal(CompactionAction.Truncate, plan.Action);
        Assert.Equal(2, plan.KvAnchor);
        Assert.Equal(3, plan.Start); // truncation starts after the anchors
    }

    // ── SliceToCompact / BuildSummarizeRequest ─────────────────────────────────

    [Fact]
    public void SliceToCompact_ReturnsExactlyThePlannedRange()
    {
        var history = History(5);
        var plan    = Decide(history, keepTurns: 2, kvAnchor: 2);
        var slice   = HistoryCompaction.SliceToCompact(history, plan);
        Assert.Equal(4, slice.Count);
        Assert.Equal("question 2", slice[0].Content); // index 3 = first non-anchored
        Assert.Equal("answer 3", slice[^1].Content);  // index 6 = last removed
    }

    [Fact]
    public void BuildSummarizeRequest_KeepsSystemPromptAndLabelsRoles()
    {
        var history = History(3);
        var slice = new List<ChatMessageDto>
        {
            new("user", "q"),
            new("assistant", "a"),
            new("tool", "t"),
            new("assistant", null),      // empty content is skipped
            new("custom-role", "c"),     // unknown roles pass through verbatim
        };
        var request = HistoryCompaction.BuildSummarizeRequest(history, slice);

        Assert.Equal(2, request.Count);
        Assert.Same(history[0], request[0]); // original system prompt, untouched
        Assert.Equal("user", request[1].Role);
        Assert.Contains("User: q", request[1].Content);
        Assert.Contains("Assistant: a", request[1].Content);
        Assert.Contains("Tool: t", request[1].Content);
        Assert.Contains("custom-role: c", request[1].Content);
    }

    // ── ApplyTruncation / ApplySummary ─────────────────────────────────────────

    [Fact]
    public void ApplyTruncation_DropsTheRangeAndKeepsTheRest()
    {
        var history = History(5); // 11 messages
        var plan    = Decide(history, keepTurns: 2);
        HistoryCompaction.ApplyTruncation(history, plan);

        Assert.Equal(5, history.Count);            // system + 2 kept turns
        Assert.Equal("system", history[0].Role);
        Assert.Equal("question 4", history[1].Content);
        Assert.Equal("answer 5", history[^1].Content);
    }

    [Fact]
    public void ApplySummary_ReplacesRangeWithSummaryPair_AfterTheAnchors()
    {
        var history = History(5);
        var plan    = Decide(history, keepTurns: 2, kvAnchor: 2);
        HistoryCompaction.ApplySummary(history, plan, "the summary");

        // system + 2 anchors + summary pair + 2 kept turns = 9 messages
        Assert.Equal(9, history.Count);
        Assert.Equal("question 1", history[1].Content);       // anchor kept verbatim
        Assert.Equal("answer 1", history[2].Content);          // anchor kept verbatim
        Assert.Equal("[Context Summary]", history[3].Content); // summary pair right after
        Assert.Equal("user", history[3].Role);
        Assert.Equal("the summary", history[4].Content);
        Assert.Equal("assistant", history[4].Role);
        Assert.Equal("question 4", history[5].Content);        // kept turns follow
    }
}
