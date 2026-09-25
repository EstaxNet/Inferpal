using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A compaction that fell back to truncation says WHY. Every failure read "Context compaction timed out" — a server
/// refusal too, and the likeliest one is permanent: a misspelled <c>utilityModel</c> fails every compaction with
/// "model not found" while the user reads "timed out" and raises a timeout that changes nothing.
/// </summary>
[Collection(CultureSerialCollection.Name)]   // compares localized sentences
public class CompactionFallbackCauseTests
{
    private static List<ChatMessageDto> LongHistory()
    {
        var h = new List<ChatMessageDto> { new("system", "sys") };
        for (var i = 0; i < 8; i++)
        {
            h.Add(new ChatMessageDto("user", $"question {i}"));
            h.Add(new ChatMessageDto("assistant", $"answer {i}"));
        }
        return h;
    }

    private static InferpalConfig Config() => new()
    {
        ContextWindowSize        = 8_192,
        ContextWindowKeepTurns   = 2,
        CompactionEnabled        = true,
        KvCacheAnchorMessages    = 0,
        CompactionTimeoutSeconds = 10,
    };

    private static Task<ContextDecision> Compact(FakeInferenceProvider client) =>
        ContextManager.PrepareAsync(LongHistory(), Config(), client, lastPromptTokens: 100_000,
                                    onStep: null, CancellationToken.None);

    [Fact]
    public async Task ARefusedSummary_NamesTheServersCause_NotATimeout()
    {
        const string cause = "Server error at http://llama.home: model 'qwen-small' not found";
        var decision = await Compact(new FakeInferenceProvider
        {
            OnChat = (_, _) => throw new AgentHttpException(cause, isTimeout: false),
        });

        Assert.Equal(ContextOutcome.CompactionFellBack, decision.Outcome);             // witness
        Assert.Contains(cause, decision.Notice);
        Assert.DoesNotContain(Strings.MsgContextCompactionFallback, decision.Notice);
        Assert.True(decision.IsDegraded);
    }

    [Fact]
    public async Task AnEmptySummary_SaysSo()
    {
        var decision = await Compact(new FakeInferenceProvider { ChatResult = new("   ", null, 0, 0) });

        Assert.Equal(ContextOutcome.CompactionFellBack, decision.Outcome);
        Assert.Equal(Strings.MsgContextCompactionEmpty, decision.Notice);
    }

    [Fact]
    public async Task AServerTimeout_IsStillATimeout()
    {
        // Reference arm: the HTTP client's own deadline is a timeout, and keeps the sentence that names one.
        var decision = await Compact(new FakeInferenceProvider
        {
            OnChat = (_, _) => throw new AgentHttpException("no answer in 120 s", isTimeout: true),
        });

        Assert.Equal(Strings.MsgContextCompactionFallback, decision.Notice);
    }

    [Fact]
    public async Task TheFuse_IsStillATimeout()
    {
        // Reference arm: compactionTimeoutSeconds blew.
        var decision = await Compact(new FakeInferenceProvider
        {
            OnChat = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new ChatTurnResult("never", null, 0, 0);
            },
        });

        Assert.Equal(Strings.MsgContextCompactionFallback, decision.Notice);
    }
}
