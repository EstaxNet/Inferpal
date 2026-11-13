using System.Collections.Generic;
using System.Linq;
using Inferpal.Models;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Covers intra-run context compaction: when accumulating tool results push the running estimate
// past num_ctx, the oldest tool results (between the anchored head and the recent tail) are elided
// so the model never loses the system prompt + task + plan to silent Ollama truncation.
public class AgentOrchestratorCompactionTests
{
    private static ChatMessageDto Tool(int chars) => new("tool", new string('x', chars));

    [Fact]
    public void EstimateTokens_IsRoughlyCharsOverFour()
    {
        var msgs = new List<ChatMessageDto> { new("user", new string('a', 400)) };
        Assert.Equal(100, AgentOrchestrator.EstimateTokens(msgs));
    }

    [Fact]
    public void EstimateTokens_CountsToolCallArguments()
    {
        // A write_file turn carries the whole file in the assistant's tool_calls, not in Content:
        // ignoring them meant a run writing large files never crossed the compaction threshold and
        // the backend silently truncated the head.
        var bigArgs = System.Text.Json.JsonSerializer.SerializeToElement(
            new { path = "a.cs", content = new string('x', 4000) });
        var call = new ToolCallDto(new ToolCallFunction("write_file", bigArgs));
        var msgs = new List<ChatMessageDto> { new("assistant", "", [call]) };

        Assert.True(AgentOrchestrator.EstimateTokens(msgs) > 1000,
            "tool-call arguments must count toward the context estimate");
    }

    [Fact]
    public void UnderBudget_LeavesMessagesUnchanged()
    {
        var msgs = new List<ChatMessageDto>
        {
            new("system", "prompt"),
            new("user", "task"),
            Tool(400),
        };
        var before = msgs.Select(m => m.Content).ToList();

        AgentOrchestrator.CompactRunContext(msgs, anchorCount: 2, budget: 8192);

        Assert.Equal(before, msgs.Select(m => m.Content).ToList());
    }

    [Fact]
    public void ZeroBudget_IsNoOp_EvenWhenLarge()
    {
        var msgs = new List<ChatMessageDto> { new("system", "s") };
        msgs.AddRange(Enumerable.Range(0, 10).Select(_ => Tool(8000)));
        var before = msgs.Select(m => m.Content).ToList();

        AgentOrchestrator.CompactRunContext(msgs, anchorCount: 1, budget: 0);

        Assert.Equal(before, msgs.Select(m => m.Content).ToList());
    }

    [Fact]
    public void OverBudget_ElidesOldestToolResults_ButPreservesHeadAndRecentTail()
    {
        // Head: system (anchored) + task. anchorCount = 2.
        var msgs = new List<ChatMessageDto>
        {
            new("system", new string('s', 4000)), // anchored head
            new("user", "execute the plan"),        // anchored head
        };
        // 10 large tool results (~2000 tokens each) → well over an 8192 budget.
        msgs.AddRange(Enumerable.Range(0, 10).Select(_ => Tool(8000)));

        AgentOrchestrator.CompactRunContext(msgs, anchorCount: 2, budget: 8192);

        // Anchored head is never touched.
        Assert.Equal(4000, msgs[0].Content!.Length);
        Assert.Equal("execute the plan", msgs[1].Content);

        // The last result (a run without an assistant message: the last tool result is its last batch) is kept whole; the
        // others are elided oldest first until the run is under the target — the recent tail too, when it does not fit.
        Assert.Equal(8000, msgs[^1].Content!.Length);
        var elided = msgs.Skip(2).Where(m => m.Content!.Length < 200).ToList();
        Assert.NotEmpty(elided);
        Assert.All(elided, m => Assert.Equal("tool", m.Role));
        Assert.True(AgentOrchestrator.EstimateTokens(msgs) <= AgentOrchestrator.CompactionBudget(8192).Target);
    }

    [Fact]
    public void Idempotent_SecondPassDoesNotDoubleElide()
    {
        var msgs = new List<ChatMessageDto> { new("system", new string('s', 4000)), new("user", "go") };
        msgs.AddRange(Enumerable.Range(0, 10).Select(_ => Tool(8000)));

        AgentOrchestrator.CompactRunContext(msgs, anchorCount: 2, budget: 8192);
        var afterFirst = msgs.Select(m => m.Content).ToList();
        AgentOrchestrator.CompactRunContext(msgs, anchorCount: 2, budget: 8192);

        Assert.Equal(afterFirst, msgs.Select(m => m.Content).ToList());
    }
}
