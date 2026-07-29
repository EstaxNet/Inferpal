using Inferpal.Config;
using Inferpal.Services.Presentation;
using Inferpal.Services.Prompting;
using Xunit;

namespace Inferpal.Tests;

// Interactive Context X-Ray panel (V2): neutral model built by XRayPanelPresenter and the
// section-suppression path of SystemPromptBuilder.Build (toggles applied to the next turn).
public class XRayPanelPresenterTests
{
    private static PromptSection Section(PromptSectionKind kind, string content, string? detail = null)
        => new(kind, detail, content);

    // ── Model shape ────────────────────────────────────────────────────────────

    [Fact]
    public void Build_OrdersSectionsByTokensAndComputesStablePercents()
    {
        var model = XRayPanelPresenter.Build(
        [
            Section(PromptSectionKind.Base,   new string('a', 1000)),   // 250 tokens
            Section(PromptSectionKind.Custom, new string('b', 3000)),   // 750 tokens
        ], disabledIds: null, historyTokens: 0, contextWindow: 0);

        Assert.Equal(2, model.Sections.Count);
        Assert.Equal("Custom", model.Sections[0].Id);                   // largest first
        Assert.Equal(75, model.Sections[0].Percent, 0);
        Assert.Equal(25, model.Sections[1].Percent, 0);
        Assert.Equal(1000, model.TotalTokens);
    }

    [Fact]
    public void Build_EmptySectionsAreDropped()
    {
        var model = XRayPanelPresenter.Build(
        [
            Section(PromptSectionKind.Base,   "abcd"),
            Section(PromptSectionKind.Custom, "   "),
        ], null, 0, 0);

        Assert.Single(model.Sections);
    }

    [Fact]
    public void Build_BaseSectionIsNotToggleable_OthersAre()
    {
        var model = XRayPanelPresenter.Build(
        [
            Section(PromptSectionKind.Base,   "abcd"),
            Section(PromptSectionKind.Memory, "efgh", ".inferpal/memory.md"),
        ], null, 0, 0);

        Assert.False(model.Sections.Single(s => s.Id == "Base").CanToggle);
        Assert.True(model.Sections.Single(s => s.Id != "Base").CanToggle);
    }

    // ── Toggles ────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_DisabledSection_ExcludedFromTotalsAndRawPrompt_ButStillListed()
    {
        var memory = Section(PromptSectionKind.Memory, "\n\n## Agent memory\n\nMEMO", ".inferpal/memory.md");
        var model  = XRayPanelPresenter.Build(
            [Section(PromptSectionKind.Base, "BASE"), memory],
            disabledIds: new HashSet<string> { XRayPanelPresenter.SectionId(memory) },
            historyTokens: 0, contextWindow: 0);

        var row = model.Sections.Single(s => s.Id.StartsWith("Memory", StringComparison.Ordinal));
        Assert.False(row.Enabled);
        Assert.Equal(1, model.TotalTokens);                             // "BASE" only
        Assert.Equal("BASE", model.RawPrompt);
        // Percent stays computed over ALL sections so bars don't reshuffle on toggle.
        Assert.True(row.Percent > 0);
    }

    [Fact]
    public void Build_RawPrompt_ReproducesBuilderOutputWithSameDisabledSet()
    {
        var config  = new InferpalConfig { CustomSystemPrompt = "Always answer in haiku." };
        var builder = new SystemPromptBuilder(config);
        var sections = builder.BuildSections("BASE", null, "\n\nTEMPLATE");
        var disabled = new HashSet<string> { "Custom" };

        var model = XRayPanelPresenter.Build(sections, disabled, 0, 0);
        var built = builder.Build("BASE", null, "\n\nTEMPLATE", disabledSectionIds: disabled);

        Assert.Equal(built, model.RawPrompt);
        Assert.DoesNotContain("haiku", built, StringComparison.Ordinal);
        Assert.Contains("TEMPLATE", built, StringComparison.Ordinal);
    }

    [Fact]
    public void SectionId_IsStableAndDistinguishesFileBackedLayers()
    {
        Assert.Equal("Base", XRayPanelPresenter.SectionId(Section(PromptSectionKind.Base, "x")));
        Assert.Equal("Pinned|a.md", XRayPanelPresenter.SectionId(Section(PromptSectionKind.Pinned, "x", "a.md")));
        Assert.NotEqual(
            XRayPanelPresenter.SectionId(Section(PromptSectionKind.Pinned, "x", "a.md")),
            XRayPanelPresenter.SectionId(Section(PromptSectionKind.Pinned, "x", "b.md")));
    }

    // ── Budget & warning ───────────────────────────────────────────────────────

    [Fact]
    public void Build_FillPercent_CountsEnabledSectionsPlusHistory()
    {
        var model = XRayPanelPresenter.Build(
            [Section(PromptSectionKind.Base, new string('a', 4000))],   // 1000 tokens
            null, historyTokens: 1000, contextWindow: 8000);

        Assert.Equal(25, model.FillPercent, 0);
    }

    [Fact]
    public void Build_OverheadWarning_TriggersWhenProjectLayersDominateTheWindow()
    {
        // 2000 overhead tokens on an 8000 window = 25% ≥ 15% threshold.
        var warn = XRayPanelPresenter.Build(
        [
            Section(PromptSectionKind.Base,  new string('a', 400)),
            Section(PromptSectionKind.Rules, new string('r', 8000), "3"),
        ], null, 0, contextWindow: 8000);

        // Same layers on a huge window: no warning.
        var calm = XRayPanelPresenter.Build(
        [
            Section(PromptSectionKind.Base,  new string('a', 400)),
            Section(PromptSectionKind.Rules, new string('r', 8000), "3"),
        ], null, 0, contextWindow: 200_000);

        Assert.True(warn.OverheadWarning);
        Assert.False(calm.OverheadWarning);
    }

    [Fact]
    public void Build_OverheadWarning_DisabledOverheadDoesNotCount()
    {
        var rules = Section(PromptSectionKind.Rules, new string('r', 8000), "3");
        var model = XRayPanelPresenter.Build(
            [Section(PromptSectionKind.Base, new string('a', 400)), rules],
            new HashSet<string> { XRayPanelPresenter.SectionId(rules) },
            0, contextWindow: 8000);

        Assert.False(model.OverheadWarning);
    }

    [Fact]
    public void Build_OverheadWarning_NoWindow_RequiresDominantAndLargeOverhead()
    {
        // 1000 overhead vs 100 base → dominant and above the absolute floor: warn.
        var warn = XRayPanelPresenter.Build(
        [
            Section(PromptSectionKind.Base,   new string('a', 400)),
            Section(PromptSectionKind.Memory, new string('m', 4000), ".inferpal/memory.md"),
        ], null, 0, contextWindow: 0);

        // Small overhead: never warn without a window.
        var calm = XRayPanelPresenter.Build(
        [
            Section(PromptSectionKind.Base,   new string('a', 400)),
            Section(PromptSectionKind.Memory, new string('m', 400), ".inferpal/memory.md"),
        ], null, 0, contextWindow: 0);

        Assert.True(warn.OverheadWarning);
        Assert.False(calm.OverheadWarning);
    }
}
