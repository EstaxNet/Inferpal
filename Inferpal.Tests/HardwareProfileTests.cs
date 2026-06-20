using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Covers the pure computation + markdown formatting of the /hardware report. The budget
// auto-seed (EnsureBudgetAsync) is not unit-tested as it shells out to nvidia-smi.
public class HardwareProfileTests
{
    const long Gb = 1_073_741_824L;

    static RunningModelInfo Running(string name, double gb) =>
        new(name, (long)(gb * Gb), "2026-01-01T00:00:00Z");

    [Fact]
    public void LoadedVramBytes_SumsRunningModels()
    {
        var p = new HardwareProfile(24, [Running("a", 4), Running("b", 2)], []);
        Assert.Equal((long)(6 * Gb), p.LoadedVramBytes);
        Assert.Equal(6, p.LoadedGb, precision: 1);
    }

    [Fact]
    public void HeadroomGb_IsBudgetMinusLoaded_WhenBudgetKnown()
    {
        var p = new HardwareProfile(24, [Running("a", 6)], []);
        Assert.Equal(18, p.HeadroomGb!.Value, precision: 1);
    }

    [Fact]
    public void HeadroomGb_Null_WhenBudgetUnknown() =>
        Assert.Null(new HardwareProfile(0, [Running("a", 6)], []).HeadroomGb);

    [Fact]
    public void IsGpu_TrueWhenAModelHoldsVram_FalseOnCpu()
    {
        Assert.True(new HardwareProfile(24, [Running("a", 6)], []).IsGpu);
        Assert.False(new HardwareProfile(24, [Running("a", 0)], []).IsGpu);
    }

    [Fact]
    public void FormatReport_NoBudget_PromptsToSetIt()
    {
        var report = new HardwareProfile(0, [], []).FormatReport();
        Assert.Contains(Strings.HardwareBudgetNotSet, report);
        Assert.Contains("/hardware <gb>", report);   // literal command name, never localized
    }

    [Fact]
    public void FormatReport_WithBudgetAndModels_ShowsHeadroomLoadedAndEstimates()
    {
        var p = new HardwareProfile(
            budgetGb: 24,
            running:  [Running("qwen:7b", 6)],
            installed:
            [
                new InstalledModelInfo("qwen:7b", 5 * Gb),
                new InstalledModelInfo("nomic-embed", 300 * 1024 * 1024L),
            ]);

        var report = p.FormatReport();

        Assert.Contains(Strings.HardwareBudgetLine("24"), report);
        Assert.Contains(Strings.HardwareHeadroom("18"), report);
        Assert.Contains(Strings.HardwareCompute("GPU"), report);
        Assert.Contains("`qwen:7b` 🟢", report);                      // loaded marker on installed table
        Assert.Contains(Strings.HardwareInstalledModelsTable, report); // estimate column header present
        Assert.Contains(Strings.HardwareInstalledNote, report);        // disclaimer present
    }

    [Fact]
    public void FormatReport_CtxAdvice_WarnsWhenConfiguredExceedsRecommended()
    {
        var advice = new ContextWindowAdvice("qwen:7b", ConfiguredCtx: 32768, RecommendedMaxCtx: 8192, ModelMaxCtx: 32768);
        var report = new HardwareProfile(24, [], [], advice).FormatReport();

        Assert.Contains(Strings.HardwareContextHeading, report);
        Assert.Contains(Strings.HardwareConfiguredCtx(32768), report);
        Assert.Contains(Strings.HardwareRecommendedCtx("qwen:7b", 8192, Strings.HardwareModelMax(32768)), report);
        Assert.Contains("⚠", report);
    }

    [Fact]
    public void FormatReport_CtxAdvice_NoWarning_WhenWithinBudget()
    {
        var advice = new ContextWindowAdvice("qwen:7b", ConfiguredCtx: 8192, RecommendedMaxCtx: 32768, ModelMaxCtx: 32768);
        var report = new HardwareProfile(24, [], [], advice).FormatReport();

        Assert.Contains(Strings.HardwareContextHeading, report);
        Assert.DoesNotContain("⚠", report);
    }

    [Fact]
    public void FormatReport_OmitsCtxSection_WhenAdviceUncomputable()
    {
        // RecommendedMaxCtx == 0 → no budget or missing arch metadata → section omitted.
        var advice = new ContextWindowAdvice("qwen:7b", ConfiguredCtx: 8192, RecommendedMaxCtx: 0, ModelMaxCtx: 0);
        var report = new HardwareProfile(0, [], [], advice).FormatReport();
        Assert.DoesNotContain(Strings.HardwareContextHeading, report);
    }
}
