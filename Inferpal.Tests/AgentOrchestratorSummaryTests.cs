using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Xunit;

// Exercises the intra-run summarisation path of CompactRunContextAsync via a fake IOllamaChatClient
// (no network). Verifies: one summary call on first overflow, replacement by a [summary] pair,
// and fallback to elision when summary is empty / disabled / already spent / under budget.
public class AgentOrchestratorSummaryTests
{
    private sealed class FakeChatClient : IOllamaChatClient
    {
        private readonly Func<ChatTurnResult> _respond;
        public int Calls { get; private set; }

        public FakeChatClient(Func<ChatTurnResult> respond) => _respond = respond;

        public Task<ChatTurnResult> SendChatAsync(
            string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal, string? toolChoice = null,
            Action<string>? onThinking = null)
        {
            Calls++;
            return Task.FromResult(_respond());
        }
    }

    private static InferpalConfig Config(bool compaction = true) =>
        new() { ContextWindowSize = 8192, CompactionEnabled = compaction };

    // Head (system + user) is anchored at index 2; then `toolCount` oversized tool results.
    private static List<ChatMessageDto> OverBudgetMessages(int toolCount = 10) =>
        new List<ChatMessageDto>
        {
            new("system", new string('s', 4000)),
            new("user", "go"),
        }
        .Concat(Enumerable.Range(0, toolCount).Select(_ => new ChatMessageDto("tool", new string('x', 8000))))
        .ToList();

    private static ChatTurnResult Reply(string text) => new(text, null, 0, 0);

    [Fact]
    public async Task FirstOverflow_SummarisesOldRange_WithOneCall()
    {
        var fake = new FakeChatClient(() => Reply("SUMMARY OF OLD TURNS"));
        var orch = new AgentOrchestrator(fake, Config());
        var msgs = OverBudgetMessages();

        var spent = await orch.CompactRunContextAsync(msgs, anchorCount: 2, model: "m",
            alreadySummarized: false, onStep: _ => { }, ct: CancellationToken.None);

        Assert.True(spent);
        Assert.Equal(1, fake.Calls);

        // Anchored head preserved.
        Assert.Equal(4000, msgs[0].Content!.Length);
        Assert.Equal("go", msgs[1].Content);

        // Old range replaced by a single [summary] pair.
        Assert.Equal("user", msgs[2].Role);
        Assert.Contains("Summary", msgs[2].Content);
        Assert.Equal("assistant", msgs[3].Role);
        Assert.Equal("SUMMARY OF OLD TURNS", msgs[3].Content);

        // The recent tail (5) survives verbatim.
        for (int i = msgs.Count - 5; i < msgs.Count; i++)
            Assert.Equal(8000, msgs[i].Content!.Length);
    }

    [Fact]
    public async Task EmptySummary_FallsBackToElision()
    {
        var fake = new FakeChatClient(() => Reply("   ")); // whitespace → treated as failure
        var orch = new AgentOrchestrator(fake, Config());
        var msgs = OverBudgetMessages();

        var spent = await orch.CompactRunContextAsync(msgs, 2, "m", false, _ => { }, CancellationToken.None);

        Assert.True(spent);
        Assert.Equal(1, fake.Calls);                                  // summary was attempted
        Assert.DoesNotContain(msgs, m => (m.Content ?? "").Contains("Summary")); // no summary pair
        Assert.Contains(msgs, m => m.Role == "tool" && m.Content!.Length < 200); // elided instead
    }

    [Fact]
    public async Task AlreadySummarised_SkipsCall_Elides()
    {
        var fake = new FakeChatClient(() => Reply("unused"));
        var orch = new AgentOrchestrator(fake, Config());
        var msgs = OverBudgetMessages();

        var spent = await orch.CompactRunContextAsync(msgs, 2, "m", alreadySummarized: true, _ => { }, CancellationToken.None);

        Assert.True(spent);
        Assert.Equal(0, fake.Calls);                                   // no second summary
        Assert.Contains(msgs, m => m.Role == "tool" && m.Content!.Length < 200);
    }

    [Fact]
    public async Task CompactionDisabled_SkipsCall_Elides()
    {
        var fake = new FakeChatClient(() => Reply("unused"));
        var orch = new AgentOrchestrator(fake, Config(compaction: false));
        var msgs = OverBudgetMessages();

        var spent = await orch.CompactRunContextAsync(msgs, 2, "m", false, _ => { }, CancellationToken.None);

        Assert.True(spent);
        Assert.Equal(0, fake.Calls);
        Assert.Contains(msgs, m => m.Role == "tool" && m.Content!.Length < 200);
    }

    [Fact]
    public async Task UnderBudget_NoCall_NoChange()
    {
        var fake = new FakeChatClient(() => Reply("unused"));
        var orch = new AgentOrchestrator(fake, Config());
        var msgs = new List<ChatMessageDto>
        {
            new("system", "prompt"),
            new("user", "go"),
            new("tool", new string('x', 400)),
        };
        var before = msgs.Select(m => m.Content).ToList();

        var spent = await orch.CompactRunContextAsync(msgs, 2, "m", false, _ => { }, CancellationToken.None);

        Assert.False(spent);
        Assert.Equal(0, fake.Calls);
        Assert.Equal(before, msgs.Select(m => m.Content).ToList());
    }
}
