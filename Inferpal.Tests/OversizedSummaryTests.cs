using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A summary is bounded by nothing but the model. Asked to keep "every key fact", a reasoning model wrote summaries as
/// long as the turns they replaced, and each compaction summarized the previous summary too: kept, they left the
/// conversation over the window, and the next requests were refused — 8 872 then 13 455 tokens on an 8 192-token window.
/// </summary>
public class OversizedSummaryTests
{
    private static InferpalConfig Config() => new()
    {
        ContextWindowSize        = 1_000,   // trigger at 800 tokens
        ContextWindowKeepTurns   = 2,
        CompactionEnabled        = true,
        KvCacheAnchorMessages    = 0,
        CompactionTimeoutSeconds = 10,
    };

    // system + 6 turns; the last two (kept) are `keptChars` long each.
    private static List<ChatMessageDto> History(int keptChars = 10)
    {
        var h = new List<ChatMessageDto> { new("system", "sys") };
        for (var i = 0; i < 6; i++)
        {
            var body = i >= 4 ? new string('k', keptChars) : $"turn {i}";
            h.Add(new ChatMessageDto("user", $"question {i} {body}"));
            h.Add(new ChatMessageDto("assistant", $"answer {i}"));
        }
        return h;
    }

    private static Task<ContextDecision> Compact(List<ChatMessageDto> history, string summary) =>
        ContextManager.PrepareAsync(history, Config(), new FakeInferenceProvider { ChatResult = new(summary, null, 0, 0) },
                                    lastPromptTokens: 100_000, onStep: null, CancellationToken.None);

    [Fact]
    public async Task ASummaryThatLeavesTheConversationOverTheWindow_FallsBackToTruncation_AndSaysWhy()
    {
        var decision = await Compact(History(), new string('s', 4_000));   // ~1 000 tokens in a 1 000-token window

        Assert.Equal(ContextOutcome.CompactionFellBack, decision.Outcome);
        Assert.Equal(Strings.MsgContextSummaryTooLong, decision.Notice);
        Assert.True(decision.IsDegraded);
    }

    [Fact]
    public async Task AShortSummary_IsKept()
    {
        // Reference arm: the ordinary summary.
        var decision = await Compact(History(), "the early turns, in short");

        Assert.Equal(ContextOutcome.Compacted, decision.Outcome);
    }

    [Fact]
    public async Task WhenTheKeptTurnsAreTheExcess_TheSummaryIsKept()
    {
        // Truncation would not fit either: the summary is not what overflows, and dropping it would lose the facts too.
        var decision = await Compact(History(keptChars: 4_000), new string('s', 400));   // one kept question alone is the window

        Assert.Equal(ContextOutcome.Compacted, decision.Outcome);
    }
}
