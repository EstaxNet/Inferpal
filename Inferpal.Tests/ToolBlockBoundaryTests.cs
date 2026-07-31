using System.Text.Json;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// History rewrites (compaction, truncation, summarisation) must never split a tool block —
/// an <c>assistant</c> carrying <c>tool_calls</c> plus the <c>tool</c> messages answering it.
/// Ollama tolerates a split block; OpenAI-compatible servers reject the whole request, so the
/// defect only ever showed up on long runs against LM Studio / vLLM / llama.cpp.
/// </summary>
public class ToolBlockBoundaryTests
{
    private static ChatMessageDto Sys()                => new("system", "you are…");
    private static ChatMessageDto User(string t = "q") => new("user", t);
    private static ChatMessageDto Asst(string t = "a") => new("assistant", t);
    private static ChatMessageDto Tool(string t = "r") => new("tool", t);

    /// <summary>An assistant message that requests <paramref name="count"/> tool calls.</summary>
    private static ChatMessageDto Calls(int count = 1)
    {
        var calls = Enumerable.Range(0, count)
            .Select(i => new ToolCallDto(new ToolCallFunction("read_file", JsonDocument.Parse($$"""{"path":"f{{i}}.cs"}""").RootElement)))
            .ToList();
        return new ChatMessageDto("assistant", null, calls);
    }

    // ── The detector ───────────────────────────────────────────────────────────

    [Fact]
    public void HasOrphanedToolMessage_WellFormedHistory_IsFalse()
    {
        List<ChatMessageDto> history = [Sys(), User(), Calls(2), Tool(), Tool(), Asst(), User(), Asst()];

        Assert.False(ToolBlockBoundary.HasOrphanedToolMessage(history));
    }

    [Fact]
    public void HasOrphanedToolMessage_ResultWithoutItsCall_IsTrue()
    {
        List<ChatMessageDto> history = [Sys(), User(), Tool(), Asst()];

        Assert.True(ToolBlockBoundary.HasOrphanedToolMessage(history));
    }

    [Fact]
    public void HasOrphanedToolMessage_CallsNeverAnsweredMidConversation_IsTrue()
    {
        List<ChatMessageDto> history = [Sys(), User(), Calls(), User("next"), Asst()];

        Assert.True(ToolBlockBoundary.HasOrphanedToolMessage(history));
    }

    [Fact]
    public void HasOrphanedToolMessage_TrailingUnansweredCalls_IsFalse()
    {
        // The in-flight state: the model just asked, the results are about to be appended.
        List<ChatMessageDto> history = [Sys(), User(), Calls()];

        Assert.False(ToolBlockBoundary.HasOrphanedToolMessage(history));
    }

    // ── Snapping ───────────────────────────────────────────────────────────────

    [Fact]
    public void SnapEnd_SwallowsTheResultsWhoseParentIsRemoved()
    {
        List<ChatMessageDto> history = [Sys(), User(), Calls(2), Tool(), Tool(), Asst()];

        // A cut right after the assistant would orphan both results.
        Assert.Equal(5, ToolBlockBoundary.SnapEnd(history, start: 2, endExclusive: 3));
    }

    [Fact]
    public void SnapStart_SwallowsTheAssistantWhoseResultsAreRemoved()
    {
        List<ChatMessageDto> history = [Sys(), User(), Calls(), Tool(), Asst()];

        // Removing from index 3 would leave the calls at index 2 unanswered.
        Assert.Equal(2, ToolBlockBoundary.SnapStart(history, 3));
    }

    [Fact]
    public void SnapEnd_ResultsWhoseParentStaysOutsideTheRange_AreLeftAlone()
    {
        // The removal takes no assistant with calls, so the tool run after it is nobody's orphan:
        // widening here would silently eat the recent tail the caller means to keep verbatim.
        List<ChatMessageDto> history = [Sys(), User(), Calls(3), Tool(), Tool(), Tool()];

        Assert.Equal(4, ToolBlockBoundary.SnapEnd(history, start: 3, endExclusive: 4));
    }

    [Fact]
    public void SnapStart_NeverGoesBelowTheFloor()
    {
        List<ChatMessageDto> history = [Sys(), Calls(), Tool()];

        Assert.Equal(1, ToolBlockBoundary.SnapStart(history, 2, floor: 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(99)]
    public void Snapping_OutOfRangeIndices_AreClamped(int index)
    {
        List<ChatMessageDto> history = [Sys(), User(), Asst()];

        Assert.InRange(ToolBlockBoundary.SnapEnd(history, 1, index), 0, history.Count);
        Assert.InRange(ToolBlockBoundary.SnapStart(history, index), 1, history.Count);
    }

    // ── Inter-turn compaction ──────────────────────────────────────────────────

    [Fact]
    public void Decide_KvAnchorCuttingThroughAToolBlock_WidensTheRemoval()
    {
        // Anchors would keep [system, user, assistant(tool_calls)] and drop the results.
        List<ChatMessageDto> history =
        [
            Sys(), User("old"), Calls(), Tool(), Asst("old answer"),
            User("mid"), Asst("mid answer"),
            User("recent"), Asst("recent answer"),
        ];

        var plan = HistoryCompaction.Decide(
            history, contextWindowSize: 100, lastPromptTokens: 95,
            keepTurnsConfig: 1, kvAnchorMessages: 2, compactionEnabled: false);

        Assert.Equal(CompactionAction.Truncate, plan.Action);

        HistoryCompaction.ApplyTruncation(history, plan);
        Assert.False(ToolBlockBoundary.HasOrphanedToolMessage(history));
    }

    [Fact]
    public void ApplySummary_AfterAToolBlockAwareplan_LeavesAValidHistory()
    {
        List<ChatMessageDto> history =
        [
            Sys(), User("old"), Calls(), Tool(), Asst("old answer"),
            User("recent"), Asst("recent answer"),
        ];

        var plan = HistoryCompaction.Decide(
            history, contextWindowSize: 100, lastPromptTokens: 95,
            keepTurnsConfig: 1, kvAnchorMessages: 2, compactionEnabled: true);

        HistoryCompaction.ApplySummary(history, plan, "…summary…");

        Assert.False(ToolBlockBoundary.HasOrphanedToolMessage(history));
        Assert.Contains(history, m => m.Content == "…summary…");
    }

    // ── The wire mapper (defence in depth) ─────────────────────────────────────

    [Fact]
    public void MapMessages_OrphanedToolResult_IsDroppedNotGivenAnInventedId()
    {
        // Whatever mangles the history, the request put on the wire stays valid: an invented
        // tool_call_id is exactly what OpenAI-compatible servers answer 400 to.
        List<ChatMessageDto> history = [Sys(), User(), Tool("orphan"), Asst("answer")];

        var mapped = OpenAiCompatibleClient.MapMessages(history);

        Assert.DoesNotContain(mapped, m => m.Role == "tool");
        Assert.Contains(mapped, m => m.Role == "assistant" && m.Content == "answer");
    }

    [Fact]
    public void MapMessages_WellFormedBlock_CorrelatesIdsPositionally()
    {
        List<ChatMessageDto> history = [Sys(), User(), Calls(2), Tool("r0"), Tool("r1")];

        var mapped = OpenAiCompatibleClient.MapMessages(history);

        var assistant = Assert.Single(mapped, m => m.ToolCalls is { Count: 2 });
        var results   = mapped.Where(m => m.Role == "tool").ToList();
        Assert.Equal(2, results.Count);
        Assert.Equal(assistant.ToolCalls![0].Id, results[0].ToolCallId);
        Assert.Equal(assistant.ToolCalls![1].Id, results[1].ToolCallId);
    }
}
