using Inferpal.Localization;
using Inferpal.Services.Commands;
using Inferpal.Services.Prompting;
using Xunit;

namespace Inferpal.Tests;

// Context X-Ray behind /xray: section-based prompt decomposition (SystemPromptBuilder.BuildSections
// must reproduce Build exactly) and the markdown rendering of XRayCommandHandler.
public class XRayCommandHandlerTests
{
    private static PromptSection Section(PromptSectionKind kind, string content, string? detail = null)
        => new(kind, detail, content);

    // ── BuildSections ↔ Build equivalence ──────────────────────────────────────

    [Fact]
    public void BuildSections_ConcatenatedContents_ReproduceBuild()
    {
        var config  = new Inferpal.Config.InferpalConfig { CustomSystemPrompt = "Always answer in haiku." };
        var builder = new SystemPromptBuilder(config);

        var built    = builder.Build("BASE PROMPT", null, "\n\n## Template\n\nreview mode");
        var sections = builder.BuildSections("BASE PROMPT", null, "\n\n## Template\n\nreview mode");

        Assert.Equal(built, string.Concat(sections.Select(s => s.Content)));
        Assert.Equal(PromptSectionKind.Base,     sections[0].Kind);
        Assert.Equal(PromptSectionKind.Custom,   sections[1].Kind);
        Assert.Equal(PromptSectionKind.Template, sections[2].Kind);
    }

    // ── Rendering ──────────────────────────────────────────────────────────────

    [Fact]
    public void Handle_RendersBarsPercentagesAndLabels()
    {
        var sections = new[]
        {
            Section(PromptSectionKind.Base,   new string('a', 3000)),          // ~750 tokens
            Section(PromptSectionKind.Custom, new string('b', 1000)),          // ~250 tokens
        };

        var text = XRayCommandHandler.Handle(sections, historyTokens: 0, contextWindow: 0, ragAutoContext: true);

        Assert.Contains(Strings.XrayLabelBase,   text);
        Assert.Contains(Strings.XrayLabelCustom, text);
        Assert.Contains("75%", text);
        Assert.Contains("25%", text);
        Assert.Contains("█", text);
        Assert.Contains("```", text);                       // aligned block
    }

    [Fact]
    public void Handle_OrdersSectionsByTokenCountDescending()
    {
        var sections = new[]
        {
            Section(PromptSectionKind.Base,   new string('a', 100)),
            Section(PromptSectionKind.Custom, new string('b', 4000)),          // bigger — must render first
        };

        var text = XRayCommandHandler.Handle(sections, 0, 0, true);

        Assert.True(text.IndexOf(Strings.XrayLabelCustom, StringComparison.Ordinal)
                  < text.IndexOf(Strings.XrayLabelBase,   StringComparison.Ordinal));
    }

    [Fact]
    public void Handle_FileSections_UseThePathAsLabel()
    {
        var sections = new[]
        {
            Section(PromptSectionKind.Base,           new string('a', 400)),
            Section(PromptSectionKind.ProjectContext, new string('c', 400), ".inferpal/context.md"),
            Section(PromptSectionKind.Pinned,         new string('p', 400), "arch.md"),
            Section(PromptSectionKind.Rules,          new string('r', 400), "2"),
        };

        var text = XRayCommandHandler.Handle(sections, 0, 0, true);

        Assert.Contains(".inferpal/context.md", text);
        Assert.Contains("📌 arch.md", text);
        Assert.Contains(Strings.XrayLabelRules("2"), text);
    }

    [Fact]
    public void Handle_ShowsHistoryRagAndBudgetLines()
    {
        var sections = new[] { Section(PromptSectionKind.Base, new string('a', 4000)) };   // ~1000 tokens

        var text = XRayCommandHandler.Handle(sections, historyTokens: 1000, contextWindow: 8000, ragAutoContext: false);

        Assert.Contains(Strings.XrayRag("off"), text);
        Assert.Contains("25", text);                        // (1000 + 1000) / 8000 = 25%
    }

    [Fact]
    public void Handle_NoContextWindow_OmitsBudgetLine()
    {
        var sections = new[] { Section(PromptSectionKind.Base, "abcd") };

        var text = XRayCommandHandler.Handle(sections, 0, contextWindow: 0, ragAutoContext: true);

        // Only the history and RAG summary bullets — no context-window line.
        Assert.Equal(2, text.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal)));
    }

    [Fact]
    public void EstimateTokens_UsesRoughCharsPerFour()
    {
        Assert.Equal(0,  XRayCommandHandler.EstimateTokens(""));
        Assert.Equal(1,  XRayCommandHandler.EstimateTokens("ab"));
        Assert.Equal(25, XRayCommandHandler.EstimateTokens(new string('x', 100)));
    }
}
