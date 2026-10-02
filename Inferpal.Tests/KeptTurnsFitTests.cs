using Inferpal.Models;
using Inferpal.Services.Agent;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Compaction keeps the last <c>contextWindowKeepTurns</c> turns verbatim, plus the first messages as a KV-cache anchor —
/// whatever their size. With 1 200-token questions on an 8 192-token window, the four kept turns and the three anchors
/// were the whole window before the tool definitions: compaction happened and the request was still refused, every turn.
/// The kept part now fits: anchors go first (a cache optimisation), then the oldest kept turns, never below one.
/// </summary>
public class KeptTurnsFitTests
{
    // system + `turns` exchanges; every question is `questionChars` long.
    private static List<ChatMessageDto> History(int turns, int questionChars)
    {
        var h = new List<ChatMessageDto> { new("system", "sys") };
        for (var i = 0; i < turns; i++)
        {
            h.Add(new ChatMessageDto("user", $"q{i} " + new string('x', questionChars)));
            h.Add(new ChatMessageDto("assistant", $"a{i}"));
        }
        return h;
    }

    [Fact]
    public void KeptTurnsThatWouldFillTheWindow_AreFewer()
    {
        // 1 000-token window, 300 tokens of tools: 600 × … the kept part may not pass 60 % — one 200-token turn fits, two do not.
        var plan = HistoryCompaction.Decide(History(6, 800), 1_000, 900, keepTurnsConfig: 4, kvAnchorMessages: 0,
                                            compactionEnabled: true, toolTokens: 300);

        Assert.Equal(CompactionAction.Compact, plan.Action);
        Assert.Equal(1, plan.KeepTurns);
    }

    [Fact]
    public void OrdinaryTurns_KeepTheConfiguredNumber()
    {
        // Reference arm: small turns fit — the setting is honoured as before.
        var plan = HistoryCompaction.Decide(History(6, 20), 1_000, 900, keepTurnsConfig: 4, kvAnchorMessages: 0,
                                            compactionEnabled: true, toolTokens: 300);

        Assert.Equal(4, plan.KeepTurns);
    }

    [Fact]
    public void AsManyTurnsAsConfigured_ButTooLargeToKeep_AreStillCompacted()
    {
        // Four questions, four kept turns configured: "nothing to compact" — yet they do not fit. One turn is kept.
        var plan = HistoryCompaction.Decide(History(4, 800), 1_000, 900, keepTurnsConfig: 4, kvAnchorMessages: 0,
                                            compactionEnabled: true, toolTokens: 300);

        Assert.Equal(CompactionAction.Compact, plan.Action);
        Assert.Equal(1, plan.KeepTurns);
    }

    [Fact]
    public void AnchorsThatDoNotFit_AreDropped_AndSmallOnesKept()
    {
        var big   = HistoryCompaction.Decide(History(8, 400), 1_000, 900, keepTurnsConfig: 1, kvAnchorMessages: 3,
                                             compactionEnabled: true, toolTokens: 300);
        var small = HistoryCompaction.Decide(History(8, 20), 1_000, 900, keepTurnsConfig: 1, kvAnchorMessages: 3,
                                             compactionEnabled: true, toolTokens: 300);

        Assert.Equal(0, big.KvAnchor);     // three 100-token messages next to a 100-token kept turn and 300 of tools
        Assert.Equal(3, small.KvAnchor);   // reference arm: the cache anchor survives when it fits
    }
}
