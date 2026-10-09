using System.IO;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Presentation;
using Inferpal.Services.Prompting;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The skills' automatic mode (<c>skillsAutoMode</c>): off by default; on, every question's prompt lists the skills —
/// the wording the triggering probe measured — and the model loads the one a request needs.
/// </summary>
public sealed class SkillsAutoModeTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"skills-auto-{Guid.NewGuid():N}");

    public SkillsAutoModeTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        RepoSkills.InvalidateCache();
    }

    public void Dispose()
    {
        RepoSkills.InvalidateCache();
        try { Directory.Delete(_repo, recursive: true); } catch { }
    }

    private void Skill(string folder, string name, string description)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_repo, folder, name));
        File.WriteAllText(Path.Combine(dir.FullName, "SKILL.md"), $"---\nname: {name}\ndescription: {description}\n---\nBody.\n");
    }

    private IReadOnlyList<PromptSection> Sections(InferpalConfig config, int window = 0, IReadOnlySet<string>? disabled = null)
    {
        RepoSkills.InvalidateCache();
        return new SystemPromptBuilder(config, contextWindow: window, workspaceRoot: _repo)
            .BuildSections("base", projectRoot: _repo, disabledSectionIds: disabled);
    }

    [Fact]
    public void Off_ByDefault_NothingIsListed()
    {
        Skill(".github/skills", "db-migration", "How to change the database schema.");

        Assert.False(new InferpalConfig().SkillsAutoMode);
        Assert.DoesNotContain(Sections(new InferpalConfig()), s => s.Kind == PromptSectionKind.Skills);
    }

    [Fact]
    public void On_ThePromptListsTheSkills_InTheWordingTheProbeMeasured()
    {
        Skill(".github/skills", "db-migration", "How to change the database schema.");
        Skill(".claude/skills", "a11y-review", "Accessibility checklist for the HTML templates.");

        var section = Assert.Single(Sections(new InferpalConfig { SkillsAutoMode = true }), s => s.Kind == PromptSectionKind.Skills);

        Assert.Contains("## Skills", section.Content);
        Assert.Contains("When the user's request matches a skill's description, first load it with "
                        + "read_skill_file(skill=\"<name>\", path=\"SKILL.md\"), then follow it. Do not load a skill that does "
                        + "not match the request.", section.Content);
        Assert.Contains("- db-migration: How to change the database schema.", section.Content);
        Assert.Contains("- a11y-review: Accessibility checklist for the HTML templates.", section.Content);
        Assert.Equal(Strings.XrayLabelSkills(section.Detail!), XRayPanelPresenter.Label(section));
        Assert.True(int.Parse(section.Detail!) >= 2, section.Detail);   // the user's own skills may add to the repository's
    }

    [Fact]
    public void On_WithoutAnySkill_AddsNothing()
    {
        Assert.DoesNotContain(Sections(new InferpalConfig { SkillsAutoMode = true }), s => s.Kind == PromptSectionKind.Skills);
    }

    [Fact]
    public void ALongDescription_IsCut_AndSaysSo()
    {
        Skill(".github/skills", "long", string.Join(" ", Enumerable.Repeat("word", 200)));

        var section = Assert.Single(Sections(new InferpalConfig { SkillsAutoMode = true }), s => s.Kind == PromptSectionKind.Skills);
        var line = section.Content.Split('\n').Single(l => l.StartsWith("- long: ", StringComparison.Ordinal));
        Assert.EndsWith("…", line);
        Assert.Equal("- long: ".Length + ModelPrompts.SkillDescriptionChars + 1, line.Length);
    }

    [Fact]
    public void ASmallWindow_CutsTheCatalog_AndSaysTheCut()
    {
        for (var i = 0; i < 120; i++) Skill(".github/skills", $"skill-{i:D3}", $"Skill number {i}, which does a precise and useful thing for the team.");

        var section = Assert.Single(Sections(new InferpalConfig { SkillsAutoMode = true }, window: 2048),
                                    s => s.Kind == PromptSectionKind.Skills);
        Assert.Matches(@"truncated to \d+ characters out of \d+", section.Content);
    }

    [Fact]
    public void TheXRaySwitch_TakesTheCatalogOut()
    {
        Skill(".github/skills", "db-migration", "How to change the database schema.");
        var config = new InferpalConfig { SkillsAutoMode = true };
        var id = XRayPanelPresenter.SectionId(Assert.Single(Sections(config), s => s.Kind == PromptSectionKind.Skills));

        RepoSkills.InvalidateCache();
        var prompt = new SystemPromptBuilder(config, workspaceRoot: _repo)
            .Build("base", projectRoot: _repo, disabledSectionIds: new HashSet<string> { id });
        Assert.DoesNotContain("db-migration", prompt);
        Assert.Contains("db-migration", new SystemPromptBuilder(config, workspaceRoot: _repo).Build("base", projectRoot: _repo));
    }

    [Fact]
    public void AToolTurnedOff_HasItsSkillsLeftOut()
    {
        Skill(".github/skills", "db-migration", "How to change the database schema.");
        Skill(".claude/skills", "a11y-review", "Accessibility checklist.");

        var section = Assert.Single(Sections(new InferpalConfig { SkillsAutoMode = true, RepoInstructionFamilies = "copilot" }),
                                    s => s.Kind == PromptSectionKind.Skills);
        Assert.Contains("db-migration", section.Content);
        Assert.DoesNotContain("a11y-review", section.Content);
    }
}
