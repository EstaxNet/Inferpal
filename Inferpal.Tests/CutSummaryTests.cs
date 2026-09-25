using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Agent;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A summary that stopped at the model's length limit is kept — it is better than dropping the turns — and SAID, to
/// both readers. Compaction runs precisely when the window is nearly full, which is when the reply has the least
/// room, and a reasoning model spends part of it thinking: the summary then ended mid-sentence, the model read it
/// as the whole earlier conversation ("as I told you earlier…" → "you never mentioned it"), and the user read
/// "conversation compacted".
/// </summary>
public class CutSummaryTests
{
    private static InferpalConfig Config() => new()
    {
        ContextWindowSize        = 1000,
        ContextWindowKeepTurns   = 2,
        CompactionEnabled        = true,
        KvCacheAnchorMessages    = 0,
        CompactionTimeoutSeconds = 10,
    };

    private static List<ChatMessageDto> LongHistory()
    {
        var h = new List<ChatMessageDto> { new("system", "sys") };
        for (var i = 0; i < 20; i++)
        {
            h.Add(new ChatMessageDto("user", $"question {i}"));
            h.Add(new ChatMessageDto("assistant", $"answer {i}"));
        }
        return h;
    }

    private static Task<ContextDecision> Compact(ChatTurnResult summary) =>
        ContextManager.PrepareAsync(LongHistory(), Config(), new FakeInferenceProvider { ChatResult = summary },
                                    lastPromptTokens: 100_000, onStep: null, ct: CancellationToken.None);

    [Fact]
    public async Task ACutConversationSummary_IsKept_AndSaidToTheModelAndTheUser()
    {
        var decision = await Compact(new ChatTurnResult("The user asked about the parser, then about the ind", null, 0, 0,
                                                        CutAtLimit: true));

        Assert.Equal(ContextOutcome.Compacted, decision.Outcome);                        // kept
        Assert.StartsWith("The user asked about the parser", decision.Summary);
        Assert.EndsWith(HistoryCompaction.CutSummaryMarker, decision.Summary);          // the model
        Assert.Contains(Strings.MsgContextSummaryCut, decision.Notice);                 // the user
        Assert.True(decision.IsDegraded);                                               // shown in clear, not folded
    }

    [Fact]
    public async Task AWholeSummary_SaysNothingMore()
    {
        // Reference arm: an ordinary compaction keeps its ordinary rendering.
        var decision = await Compact(new ChatTurnResult("The user asked about the parser.", null, 0, 0));

        Assert.Equal("The user asked about the parser.", decision.Summary);
        Assert.DoesNotContain(Strings.MsgContextSummaryCut, decision.Notice);
        Assert.False(decision.IsDegraded);
    }

    private sealed class FakeChatClient(ChatTurnResult reply) : IOllamaChatClient
    {
        public Task<ChatTurnResult> SendChatAsync(
            string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal, string? toolChoice = null,
            Action<string>? onThinking = null) => Task.FromResult(reply);
    }

    [Fact]
    public async Task ACutRunSummary_IsSaidToTheModel()
    {
        var orch = new AgentOrchestrator(
            new FakeChatClient(new ChatTurnResult("Read Foo.cs and Bar.cs; the bug is in", null, 0, 0, CutAtLimit: true)),
            new InferpalConfig { ContextWindowSize = 8192, CompactionEnabled = true });
        var msgs = new List<ChatMessageDto> { new("system", new string('s', 4000)), new("user", "go") }
            .Concat(Enumerable.Range(0, 10).Select(_ => new ChatMessageDto("tool", new string('x', 8000))))
            .ToList();

        await orch.CompactRunContextAsync(msgs, anchorCount: 2, model: "m", alreadySummarized: false,
                                          onStep: _ => { }, ct: CancellationToken.None);

        var summary = msgs[3];
        Assert.Equal("assistant", summary.Role);
        Assert.StartsWith("Read Foo.cs and Bar.cs", summary.Content);                   // witness: summarised
        Assert.EndsWith(HistoryCompaction.CutSummaryMarker, summary.Content);
    }
}
