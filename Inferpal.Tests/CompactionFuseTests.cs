using System.IO;
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

    private static List<ChatMessageDto> ShortHistory()
    {
        // Short turns: the time to read rounds to nothing, so the fuse is the configured 10 s.
        var h = new List<ChatMessageDto> { new("system", "sys") };
        for (var i = 0; i < 6; i++)
        {
            h.Add(new ChatMessageDto("user", $"question {i}"));
            h.Add(new ChatMessageDto("assistant", $"answer {i}"));
        }
        return h;
    }

    private static InferpalConfig TenSecondFuse() => new()
    {
        ContextWindowSize = 8_192, ContextWindowKeepTurns = 2, CompactionEnabled = true,
        KvCacheAnchorMessages = 0, CompactionTimeoutSeconds = 10,
    };

    /// <summary>
    /// A reasoning model streams its thinking long before the first word of the summary: 50 to 129 s on a 27B model.
    /// Counted from the start, the fuse fired during that reasoning — every chunk now rearms it.
    /// </summary>
    [Fact]
    public async Task AModelThatKeepsReasoning_PastTheFuse_IsWaitedFor()
    {
        Action<string>? think = null;
        var client = new FakeInferenceProvider
        {
            DriveThinking = t => think = t,
            OnChat = async (_, ct) =>
            {
                for (var i = 0; i < 26; i++)        // 13 s of reasoning, a chunk every half second
                {
                    await Task.Delay(500, ct);
                    think?.Invoke("thinking… ");
                }
                return new ChatTurnResult("summary of the early turns", null, 0, 0);
            },
        };

        var decision = await ContextManager.PrepareAsync(ShortHistory(), TenSecondFuse(), client, lastPromptTokens: 100_000,
                                                         onStep: null, CancellationToken.None);

        Assert.NotNull(think);                                                         // witness: reasoning was streamed
        Assert.Equal(ContextOutcome.Compacted, decision.Outcome);
    }

    [Fact]
    public async Task AModelThatStaysSilent_PastTheFuse_StillTripsIt()
    {
        // Reference arm: the fuse still guards against a model that does not produce anything. ⚠ The model answers only
        // after two minutes, so the fuse (~11 s) is the only way out: a 13 s answer raced it, and a loaded CI runner,
        // late on its timers, let the answer win.
        var client = new FakeInferenceProvider
        {
            OnChat = async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromMinutes(2), ct);
                return new ChatTurnResult("too late", null, 0, 0);
            },
        };

        var decision = await ContextManager.PrepareAsync(ShortHistory(), TenSecondFuse(), client, lastPromptTokens: 100_000,
                                                         onStep: null, CancellationToken.None);

        Assert.Equal(ContextOutcome.CompactionFellBack, decision.Outcome);
        Assert.Equal(Strings.MsgContextCompactionFallback, decision.Notice);
    }

    /// <summary>Both summarizing calls rearm the same fuse on both streams — the agent run's is not executable without
    /// a whole run, so it is read in the source.</summary>
    [Theory]
    [InlineData("ContextManager.cs")]
    [InlineData("AgentOrchestrator.cs")]
    public void BothSummarizingCalls_RearmTheFuseOnEveryChunk(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(dir!.FullName, "Inferpal.Core", "Services", "Agent", file));

        var at = code.IndexOf("new SummaryFuse(", StringComparison.Ordinal);
        Assert.True(at >= 0, "the fuse is gone from this summarizing call");          // WITNESS
        var send = code.IndexOf("SendChatAsync(", at, StringComparison.Ordinal);
        var call = code[send..code.IndexOf(';', send)];
        Assert.Contains("fuse.Progress, cts.Token", call.Replace("onToken: ", ""), StringComparison.Ordinal);
        Assert.Contains("onThinking: fuse.Progress", call, StringComparison.Ordinal);
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
