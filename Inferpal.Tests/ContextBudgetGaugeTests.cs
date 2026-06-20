using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// ContextBudgetGauge owns the context-window fill indicator's threshold logic (the 50/80/95%
// colour steps) that used to be inline in the VM. Numbers are invariant-formatted, so the tooltip
// assertions are culture-independent (the suite runs under FR culture).
public class ContextBudgetGaugeTests
{
    [Theory]
    [InlineData(0, 8000)]      // no prompt measured yet
    [InlineData(1000, 0)]      // no limit configured
    [InlineData(1000, -1)]     // invalid limit
    public void Compute_ReturnsNull_WhenNothingToShow(int tokens, int limit) =>
        Assert.Null(ContextBudgetGauge.Compute(tokens, limit));

    [Theory]
    [InlineData(1000, 8000, "#606060")]  // 12.5% → green
    [InlineData(5000, 8000, "#C0A000")]  // 62.5% → amber
    [InlineData(7000, 8000, "#D06000")]  // 87.5% → orange
    [InlineData(7800, 8000, "#CC2222")]  // 97.5% → red
    public void Compute_PicksSeverityColourByThreshold(int tokens, int limit, string expectedColor) =>
        Assert.Equal(expectedColor, ContextBudgetGauge.Compute(tokens, limit)!.Color);

    [Fact]
    public void Compute_ClampsFillAt100Percent()
    {
        var b = ContextBudgetGauge.Compute(20_000, 8000)!;
        Assert.Equal(100.0, b.FillPercent);
        Assert.Equal("#CC2222", b.Color);
    }

    [Fact]
    public void Compute_TooltipIsInvariantFormatted()
    {
        var b = ContextBudgetGauge.Compute(1500, 8000)!;
        Assert.Equal("Context: 1,500 / 8,000 tokens (19%)", b.Tooltip);
    }

    [Fact]
    public void Compute_ColourBoundaries_AreExclusiveLowerInclusive()
    {
        // Exactly 50% leaves green (pct < 50 is false) → amber; exactly 80% → orange; 95% → red.
        Assert.Equal("#C0A000", ContextBudgetGauge.Compute(4000, 8000)!.Color); // 50%
        Assert.Equal("#D06000", ContextBudgetGauge.Compute(6400, 8000)!.Color); // 80%
        Assert.Equal("#CC2222", ContextBudgetGauge.Compute(7600, 8000)!.Color); // 95%
    }
}
