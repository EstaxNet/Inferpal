using Inferpal.Services.Bench;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>/bench</c> counted tokens it could not time, and the inflated rate is what picks a model.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ A task whose reply never streams has no measurable generation time: time-to-first-token
/// swallows the whole call, so <c>seconds</c> comes out 0 — and its tokens were still added to the
/// numerator of the aggregate. The tool-call task is exactly that case, on <b>every</b> run: its
/// reply is a tool call, the comment in <c>TimedChatAsync</c> names it ("no streaming
/// (tool-call-only reply) → whole call"), and most backends still report usage for it. Free tokens,
/// systematically, by an amount that depends on what each backend reports — inside the one command
/// whose purpose is to compare backends and models.
/// </para>
/// <para>
/// ⚠ <b>And the number is not decorative.</b> <c>BenchCommandHandler.Recommend</c> sorts the utility
/// and FIM roles by <c>TokensPerSec</c> and uses it to break ties for the agent role, and
/// <c>ModelRouter.ResolveUtilityAsync</c> reads that recommendation when auto-routing is on. A rate
/// biased per backend changes which model answers.
/// </para>
/// <para>
/// Measured as arithmetic, not against a clock: the aggregation is a pure function, so the defect is
/// a sample of (tokens, 0 s) counting towards a rate.
/// </para>
/// </remarks>
public sealed class BenchThroughputTests
{
    [Fact]
    public void ASampleWithNoMeasurableTime_DoesNotContributeItsTokens()
    {
        // 100 tokens in 1 s, plus 50 tokens in no time at all. The honest rate is 100/s.
        var rate = BenchRunner.Throughput([(100, 1.0), (50, 0.0)]);

        Assert.Equal(100.0, rate, 3);
    }

    [Fact]
    public void SamplesThatWereTimed_StillAggregate()
    {
        // REFERENCE ARM: the aggregate is over the whole run, not the best task — a fix that kept
        // only one sample would pass the test above.
        var rate = BenchRunner.Throughput([(100, 1.0), (200, 1.0)]);

        Assert.Equal(150.0, rate, 3);
    }

    [Fact]
    public void WhenNothingCouldBeTimed_TheRateIsUnknown_NotZero()
    {
        Assert.Equal(0.0, BenchRunner.Throughput([(50, 0.0), (80, 0.0)]));
        Assert.Equal(0.0, BenchRunner.Throughput([]));
    }

    [Fact]
    public void TheReport_ShowsAnUnknownRateAsUnknown_NotAsZero()
    {
        // ⚠ "0.0" in the speed column is a claim: it reads as a model that generates nothing. The
        // VRAM column already spells its own unknown as "—"; this one printed a number.
        var md = BenchCommandHandler.FormatReport(
            [new BenchModelResult("m", TtftMs: 120, TokensPerSec: 0, VramBytes: -1,
                                  QualityScore: 4, QualityMax: 5, FimPassed: false, Error: null)],
            savedAtUtc: null);

        Assert.Contains("`m`", md, StringComparison.Ordinal);      // witness: the row is there
        Assert.Contains("4/5", md, StringComparison.Ordinal);      // and it is the real row
        Assert.DoesNotContain("| 0.0 |", md, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReport_StillPrintsAMeasuredRate()
    {
        // REFERENCE ARM: only the unknown becomes a dash.
        var md = BenchCommandHandler.FormatReport(
            [new BenchModelResult("m", TtftMs: 120, TokensPerSec: 42.5, VramBytes: -1,
                                  QualityScore: 4, QualityMax: 5, FimPassed: false, Error: null)],
            savedAtUtc: null);

        Assert.Contains("42.5", md, StringComparison.Ordinal);
    }
}
