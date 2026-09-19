using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Hardware;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The screen you open to ask "is my context window sane?" answers — including in the case where it
/// is most likely wrong.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Measured: with the model's weights larger than the declared VRAM budget — a partially
/// offloaded model, the most ordinary shape of "my context is too big" — <c>MaxSafeNumCtx</c>
/// returns 0 and the whole <i>Context window</i> section of <c>/hardware</c> <b>vanished</b>. The
/// model's own ceiling was in hand and owes nothing to VRAM.
/// </para>
/// <para>
/// ⚠ And the warning named the wrong cause. Over the VRAM recommendation the KV cache spills to
/// system RAM: it is <b>slower</b>. Over the model's own context length the request cannot be
/// served at all — the backend drops the head of the prompt, system prompt included, and this
/// product's own compaction never fires because <c>HistoryCompaction.Decide</c> measures against
/// the CONFIGURED number. The measured sentence spoke of "KV-cache spilling to system RAM and
/// slowing generation", which sends that user to think about speed.
/// </para>
/// </remarks>
public sealed class ContextCeilingTests
{
    private static ModelArchInfo Arch(int contextLength) =>
        new(BlockCount: 32, HeadCount: 32, HeadCountKv: 8, EmbeddingLength: 4096,
            ContextLength: contextLength, KeyLength: 128, ValueLength: 128);

    private static string Report(int budgetGb, int configured, int recommended, int modelMax) =>
        new HardwareProfile(budgetGb, [], [],
            new ContextWindowAdvice("m", configured, recommended, modelMax)).FormatReport();

    [Fact]
    public void WeightsBiggerThanTheBudget_StillReportTheModelsOwnCeiling()
    {
        // WITNESS: this is the configuration that produced no recommendation at all.
        var recommended = ModelCatalog.MaxSafeNumCtx(budgetGb: 8, weightsBytes: 12L << 30, Arch(8192));
        Assert.Equal(0, recommended);

        var report = Report(8, configured: 32768, recommended, modelMax: 8192);

        Assert.Contains(Strings.HardwareContextHeading, report);
        Assert.Contains(Strings.HardwareCtxNoVramRecommendation("m", 8192), report);
    }

    [Fact]
    public void OverTheModelsCeiling_NamesThatCause_NotTheVramOne()
    {
        var report = Report(24, configured: 32768, recommended: 8192, modelMax: 8192);

        Assert.Contains(Strings.HardwareCtxExceedsModel(32768, 8192), report);
        Assert.DoesNotContain(Strings.HardwareCtxWarn(32768, 8192), report);
    }

    /// <summary>Reference arm: the VRAM ceiling alone keeps the sentence it always had.</summary>
    [Fact]
    public void OverTheVramRecommendationOnly_KeepsTheVramSentence()
    {
        // A model whose own limit is far away (128k), a budget that only affords 16k.
        var report = Report(8, configured: 32768, recommended: 16384, modelMax: 131072);

        Assert.Contains(Strings.HardwareCtxWarn(32768, 16384), report);
        Assert.DoesNotContain(Strings.HardwareCtxExceedsModel(32768, 131072), report);
    }

    /// <summary>Reference arm: a configuration that fits both ceilings warns about nothing.</summary>
    [Fact]
    public void AConfigurationThatFitsBoth_WarnsAboutNothing()
    {
        var report = Report(24, configured: 8192, recommended: 16384, modelMax: 131072);

        Assert.Contains(Strings.HardwareContextHeading, report);
        Assert.DoesNotContain("⚠", report);
    }

    /// <summary>Reference arm: nothing known about the model at all — no section, as before.</summary>
    [Fact]
    public void WithNeitherCeiling_TheSectionIsStillOmitted()
    {
        var report = Report(24, configured: 8192, recommended: 0, modelMax: 0);

        Assert.DoesNotContain(Strings.HardwareContextHeading, report);
    }
}
