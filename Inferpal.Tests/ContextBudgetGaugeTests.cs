using System.Globalization;
using Inferpal.Localization;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// ContextBudgetGauge owns the context-window fill indicator's threshold logic (the 50/80/95%
// colour steps) that used to be inline in the VM. Its tooltip is LOCALIZED, and says word for word
// what the VS Code panel says - the two describe the same clickable element. The numbers follow
// CultureInfo.CurrentCulture, which ApplyLanguage does NOT change: assertions never pin a separator.
//
// Serialized: one test here flips Strings.OverrideCulture, which is process-wide.
[Collection(CultureSerialCollection.Name)]
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
    public void Compute_TooltipIsLocalized_AndSaysWhatTheVsCodePanelSays()
    {
        // ⚠ It was English, with invariant separators, while the VS Code panel said the same thing
        // translated - about an element that does exactly the same thing on both sides (clicking it
        // opens the X-Ray). The original comment justified the English by the NUMBERS, an argument
        // that says nothing about the language.
        try
        {
            Strings.ApplyLanguage("en");
            var english = ContextBudgetGauge.Compute(1500, 8000)!.Tooltip;
            Assert.Contains("click for the X-Ray panel", english, StringComparison.Ordinal);

            Strings.ApplyLanguage("fr");
            var french = ContextBudgetGauge.Compute(1500, 8000)!.Tooltip;
            Assert.NotEqual(english, french);
            Assert.DoesNotContain("click for the X-Ray panel", french, StringComparison.Ordinal);

            // ⚠ The NUMBERS follow CultureInfo.CurrentCulture, which ApplyLanguage does NOT change:
            // the product refuses by doctrine to mutate CurrentUICulture, so ApplyLanguage only
            // swaps the resources. Hardcoding "1,500" here made the test pass or fail depending on
            // the machine, not on the product (paid for on a French box while writing it).
            var formatted = 1500.ToString("N0", CultureInfo.CurrentCulture);
            Assert.Contains(formatted, english, StringComparison.Ordinal);
            Assert.Contains(formatted, french,  StringComparison.Ordinal);
        }
        finally { Strings.ApplyLanguage(null); }
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
