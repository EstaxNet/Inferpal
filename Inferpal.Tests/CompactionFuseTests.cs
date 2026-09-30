using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ The compaction fuse guards against a model that never answers, not against a slow one. A fixed one fired on
/// reading alone — a model reads the whole slice before writing a word, and the slice is largest when the conversation
/// outgrows its window — so the summary became a hard truncation that kept nothing.
/// </summary>
public sealed class CompactionFuseTests
{
    private static SummarizeRequest RequestOf(int chars) =>
        HistoryCompaction.BuildSummarizeRequest([new ChatMessageDto("user", new string('x', chars))]);

    [Fact]
    public void TheFuse_IsTheConfiguredWritingTime_PlusTheTimeToReadTheRequest()
    {
        var small = HistoryCompaction.SummaryTimeout(45, RequestOf(100));
        var large = HistoryCompaction.SummaryTimeout(45, RequestOf(40_000));

        Assert.InRange(small.TotalSeconds, 45, 47);               // a short slice keeps the configured fuse
        // 40 000 characters ≈ 10 000 tokens, read at the fuse's 100 tokens/s: ~100 s on top of the 45.
        Assert.InRange(large.TotalSeconds, 140, 150);
    }

    [Fact]
    public void TheConfiguredSeconds_KeepTheirFloorOfTen() =>
        Assert.InRange(HistoryCompaction.SummaryTimeout(0, RequestOf(100)).TotalSeconds, 10, 12);

    private static List<ChatMessageDto> LongHistory()
    {
        var h = new List<ChatMessageDto> { new("system", "sys") };
        for (var i = 0; i < 8; i++)
        {
            h.Add(new ChatMessageDto("user", $"question {i} " + new string('q', 1_000)));
            h.Add(new ChatMessageDto("assistant", $"answer {i} " + new string('a', 1_000)));
        }
        return h;
    }

    [Fact]
    public async Task ASlowSummaryOfALongSlice_IsWaitedFor_WhereTheFixedFuseTruncated()
    {
        var config = new InferpalConfig
        {
            ContextWindowSize = 8_192, ContextWindowKeepTurns = 2, CompactionEnabled = true,
            KvCacheAnchorMessages = 0, CompactionTimeoutSeconds = 10,
        };
        var client = new FakeInferenceProvider
        {
            // Past the configured 10 s, well within 10 s + the time to read the slice.
            OnChat = async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(12), ct);
                return new ChatTurnResult("summary of the early turns", null, 0, 0);
            },
        };

        var decision = await ContextManager.PrepareAsync(LongHistory(), config, client, lastPromptTokens: 100_000,
                                                         onStep: null, CancellationToken.None);

        Assert.Equal(ContextOutcome.Compacted, decision.Outcome);
        Assert.DoesNotContain(Strings.MsgContextCompactionFallback, decision.Notice ?? "");
    }

    [Fact]
    public void TheTimeoutMessage_NamesTheSettingThatAvoidsIt()
    {
        // The setting's own label, in the current language, without its "(safety fuse):" tail.
        var name = Strings.LabelCompactionTimeout.Split('(', '（')[0].Trim();
        Assert.True(name.Length >= 4, $"label read: '{Strings.LabelCompactionTimeout}'");   // witness
        Assert.Contains(name, Strings.MsgContextCompactionFallback);
    }
}
