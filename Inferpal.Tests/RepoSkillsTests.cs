using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Host;
using Inferpal.Localization;
using Inferpal.Services.Commands;
using Inferpal.Services.Execution;
using Inferpal.Services.Prompting;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The skills a repository — or the user — wrote for Copilot, Claude Code and agents (<c>SKILL.md</c> folders) are
/// listed by <c>/skill</c>, joined to a question by <c>/skill &lt;name&gt; &lt;request&gt;</c>, and their files read by
/// <c>read_skill_file</c>, confined to the skill's folder. A script runs only through <c>run_command</c>, approved.
/// </summary>
public sealed class RepoSkillsTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"repo-skills-{Guid.NewGuid():N}");
    private readonly string _home = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"home-skills-{Guid.NewGuid():N}");

    public RepoSkillsTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Directory.CreateDirectory(_home);
        RepoSkills.InvalidateCache();
    }

    public void Dispose()
    {
        RepoSkills.InvalidateCache();
        try { Directory.Delete(_repo, recursive: true); } catch { }
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    private string Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static string SkillMd(string name, string? description, string body = "Do the thing.", string extra = "") =>
        "---\nname: " + name + "\n" + (description is null ? "" : "description: " + description + "\n") + extra + "---\n" + body + "\n";

    private SkillCatalog Catalog(InferpalConfig? config = null) =>
        RepoSkills.Read(_repo, RepoInstructionFormats.Families((config ?? new InferpalConfig()).RepoInstructionFamilies).On, _home);

    private SkillCommandHandler.Result Skill(string typed, InferpalConfig? config = null)
    {
        RepoSkills.InvalidateCache();
        return SkillCommandHandler.Run(config ?? new InferpalConfig(), _repo, typed.Split(' ', StringSplitOptions.RemoveEmptyEntries), _home);
    }

    // ── The catalog ─────────────────────────────────────────────────────────

    [Fact]
    public void SkillsAreFoundInEveryRepositoryFolder_WithTheirToolNamed()
    {
        Write(_repo, ".github/skills/api-review/SKILL.md", SkillMd("api-review", "Review a public API"));
        Write(_repo, ".claude/skills/pdf/SKILL.md", SkillMd("pdf", "Extract text from PDF files"));
        Write(_repo, ".agents/skills/release/SKILL.md", SkillMd("release", "Cut a release"));
        Write(_repo, ".cline/skills/publish/SKILL.md", SkillMd("publish", "Publish the extension"));
        Write(_repo, ".roo/skills/translate/SKILL.md", SkillMd("translate", "Translate the UI"));

        var skills = Catalog().Skills;
        Assert.Equal(["api-review", "pdf", "release", "publish", "translate"], skills.Select(s => s.Name));
        Assert.Equal(["GitHub Copilot", "Claude Code", "Agent Skills", "Cline", "Roo Code"], skills.Select(s => s.Origin));
        Assert.Equal("Extract text from PDF files", skills[1].Description);
    }

    [Fact]
    public void AUserSkill_IsFound_AndARepositorySkillOfTheSameNameWins()
    {
        Write(_home, ".claude/skills/pdf/SKILL.md", SkillMd("pdf", "My own PDF skill"));
        Write(_home, ".agents/skills/notes/SKILL.md", SkillMd("notes", "Keep notes"));
        Write(_repo, ".github/skills/pdf/SKILL.md", SkillMd("pdf", "The team's PDF skill"));

        var catalog = Catalog();
        Assert.Equal("The team's PDF skill", catalog.Find("pdf")!.Description);
        Assert.Equal("Keep notes", catalog.Find("notes")!.Description);
        var shadowed = Assert.Single(catalog.Skipped, s => s.Reason == SkillSkipReason.Shadowed);
        Assert.Equal(".github/skills/pdf", shadowed.By);
    }

    [Fact]
    public void ASkillWithoutADescription_IsNotLoaded_AndListingSaysWhy()
    {
        Write(_repo, ".claude/skills/half/SKILL.md", SkillMd("half", null));

        Assert.Empty(Catalog().Skills);
        Assert.Contains(Strings.SkillNoDescription, Skill("/skill").Message);
    }

    [Fact]
    public void ANameThatCannotBeTyped_UsesTheFolderName()
    {
        Write(_repo, ".claude/skills/pdf-tools/SKILL.md", SkillMd("PDF Tools", "Work with PDFs"));

        Assert.Equal("pdf-tools", Assert.Single(Catalog().Skills).Name);
    }

    [Fact]
    public void AToolTurnedOffInTheSettings_HasItsSkillsLeftUnread()
    {
        Write(_repo, ".claude/skills/pdf/SKILL.md", SkillMd("pdf", "Extract text"));
        Write(_repo, ".github/skills/api/SKILL.md", SkillMd("api", "Review an API"));

        var names = Catalog(new InferpalConfig { RepoInstructionFamilies = "copilot" }).Skills.Select(s => s.Name);
        Assert.Equal(["api"], names);
    }

    // ── /skill ──────────────────────────────────────────────────────────────

    [Fact]
    public void Skill_Lists_WithDescriptionAndOrigin()
    {
        Write(_repo, ".claude/skills/pdf/SKILL.md", SkillMd("pdf", "Extract text from PDF files"));

        var list = Skill("/skill").Message!;
        Assert.Contains("`pdf`", list);
        Assert.Contains("Extract text from PDF files", list);
        Assert.Contains("Claude Code", list);
        Assert.Contains(".claude/skills/pdf", list);
    }

    [Fact]
    public void Skill_WithoutAnySkill_IsOneSentence()
    {
        Assert.Equal(Strings.SkillsNone, Skill("/skill").Message);
    }

    [Fact]
    public void SkillPdf_JoinsTheBodyAndItsFiles_ToTheQuestion()
    {
        Write(_repo, ".claude/skills/pdf/SKILL.md",
              SkillMd("pdf", "Extract text from PDF files", "Run scripts/extract.py on the file, then summarize.",
                      "allowed-tools: Bash(python:*)\n"));
        Write(_repo, ".claude/skills/pdf/scripts/extract.py", "print('text')\n");
        Write(_repo, ".claude/skills/pdf/reference.md", "# Reference\n");

        var r = Skill("/skill pdf summarize this file");

        Assert.Null(r.Message);
        Assert.Equal("summarize this file", r.Question);
        Assert.Equal(Strings.SkillAttachmentLabel("pdf"), r.Attachment!.Label);
        var content = r.Attachment.Content;
        Assert.Contains("Run scripts/extract.py on the file, then summarize.", content);
        Assert.DoesNotContain("allowed-tools", content);                         // the front matter stays out
        Assert.Contains("scripts/extract.py", content);
        Assert.Contains("reference.md", content);
        Assert.Contains(Path.Combine(_repo, ".claude", "skills", "pdf"), content);  // where the scripts are
    }

    [Fact]
    public void SkillPdf_WithoutARequest_StillAsksSomething()
    {
        Write(_repo, ".claude/skills/pdf/SKILL.md", SkillMd("pdf", "Extract text"));

        Assert.Equal(Strings.SkillApplyDefault("pdf"), Skill("/skill pdf").Question);
    }

    [Fact]
    public void AnUnknownSkill_IsNamed_WithTheOnesThatExist()
    {
        Write(_repo, ".claude/skills/pdf/SKILL.md", SkillMd("pdf", "Extract text"));

        var message = Skill("/skill pfd x").Message!;
        Assert.Contains(Strings.SkillUnknown("pfd"), message);
        Assert.Contains("`pdf`", message);
    }

    [Fact]
    public void ALongFileIndex_IsCut_AndSaysHowManyAreLeftOut()
    {
        Write(_repo, ".claude/skills/big/SKILL.md", SkillMd("big", "Many files"));
        for (var i = 0; i < RepoSkills.MaxIndexedFiles + 7; i++) Write(_repo, $".claude/skills/big/refs/f{i:D3}.md", "x");

        var content = Skill("/skill big go").Attachment!.Content;
        Assert.Contains(ModelPrompts.SkillFilesOmitted(7), content);
        Assert.Contains("refs/f000.md", content);
        Assert.DoesNotContain($"refs/f{RepoSkills.MaxIndexedFiles + 6:D3}.md", content);
    }

    // ── read_skill_file ─────────────────────────────────────────────────────

    private ReadSkillFileTool Tool(InferpalConfig? config = null) => new(() => _repo, config ?? new InferpalConfig(), () => _home);

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task ReadSkillFile_ReadsAFileOfTheSkill_AUserSkillOutsideTheWorkspaceIncluded()
    {
        Write(_home, ".claude/skills/notes/SKILL.md", SkillMd("notes", "Keep notes"));
        Write(_home, ".claude/skills/notes/templates/day.md", "DAY-TEMPLATE");

        var text = await Tool().ExecuteAsync(Args(new { skill = "notes", path = "templates/day.md" }), default);
        Assert.Contains("DAY-TEMPLATE", text);
    }

    [Theory]
    [InlineData("../../../.git/config")]
    [InlineData("..\\other\\SKILL.md")]
    public async Task ReadSkillFile_RefusesAPathOutOfTheSkillsFolder(string path)
    {
        Write(_repo, ".claude/skills/pdf/SKILL.md", SkillMd("pdf", "Extract text"));
        Write(_repo, ".claude/skills/other/SKILL.md", SkillMd("other", "OTHER-SECRET"));
        Write(_repo, ".git/config", "[core] OTHER-SECRET");

        var text = await Tool().ExecuteAsync(Args(new { skill = "pdf", path }), default);
        Assert.DoesNotContain("OTHER-SECRET", text);
        Assert.StartsWith("Error:", text);
    }

    [Fact]
    public async Task ReadSkillFile_RefusesAnAbsolutePathElsewhere()
    {
        Write(_repo, ".claude/skills/pdf/SKILL.md", SkillMd("pdf", "Extract text"));
        var elsewhere = Write(_repo, "secrets.txt", "WORKSPACE-SECRET");

        var text = await Tool().ExecuteAsync(Args(new { skill = "pdf", path = elsewhere }), default);
        Assert.DoesNotContain("WORKSPACE-SECRET", text);
    }

    [Fact]
    public async Task ReadSkillFile_RefusesALinkLeavingTheSkillsFolder()
    {
        Write(_repo, ".claude/skills/pdf/SKILL.md", SkillMd("pdf", "Extract text"));
        var outside = Write(_repo, "secrets.txt", "LINKED-SECRET");
        var link = Path.Combine(_repo, ".claude", "skills", "pdf", "leak.md");
        try { File.CreateSymbolicLink(link, outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }   // no privilege: undecided

        var text = await Tool().ExecuteAsync(Args(new { skill = "pdf", path = "leak.md" }), default);
        Assert.DoesNotContain("LINKED-SECRET", text);
    }

    [Fact]
    public async Task ReadSkillFile_NamesTheSkillsThatExist_ForAnUnknownOne()
    {
        Write(_repo, ".claude/skills/pdf/SKILL.md", SkillMd("pdf", "Extract text"));

        var text = await Tool().ExecuteAsync(Args(new { skill = "pfd", path = "SKILL.md" }), default);
        Assert.Contains("pdf", text);
        Assert.StartsWith("Error:", text);
    }

    [Fact]
    public void ReadSkillFile_IsOffered_OnlyWhenThereIsASkill()
    {
        Assert.False(((ITool)Tool()).IsOffered);                                // reference arm: no skill, no tool
        Write(_repo, ".claude/skills/pdf/SKILL.md", SkillMd("pdf", "Extract text"));
        RepoSkills.InvalidateCache();
        Assert.True(((ITool)Tool()).IsOffered);
    }

    // ── Writing a skill asks ────────────────────────────────────────────────

    [Theory]
    [InlineData(".github/skills/pdf/SKILL.md", true)]
    [InlineData("C:/repo/.claude/skills/pdf/scripts/extract.py", true)]
    [InlineData("/home/me/.agents/skills/notes/templates/day.md", true)]
    [InlineData(".cline/skills/publish/SKILL.md", true)]
    [InlineData("src/skills/pdf/SKILL.md", false)]                 // reference arm: not a skills folder of any tool
    [InlineData(".github/workflows/build.yml", false)]
    public void WritingIntoASkillsFolder_ForcesTheApproval(string path, bool forced)
    {
        Assert.Equal(forced, AgentInstructionFiles.Targets(path));
    }
}

public partial class HostServerTests
{
    /// <summary>
    /// VS Code: <c>/skill pdf …</c> joins the skill to the question (a chip, never the body as the question), and the
    /// skill's script runs only through <c>run_command</c>, asked — <c>allowed-tools</c> in its front matter grants
    /// nothing.
    /// </summary>
    [Fact]
    public async Task ASkill_IsJoinedToTheQuestion_AndItsScriptIsApproved()
    {
        using var h = CreateHarness();
        h.Target.ApprovalAnswer = 1;
        var skill = Path.Combine(h.RootDir, ".claude", "skills", "pdf");
        Directory.CreateDirectory(Path.Combine(skill, "scripts"));
        File.WriteAllText(Path.Combine(skill, "SKILL.md"),
            "---\nname: pdf\ndescription: Extract text from PDF files\nallowed-tools: Bash(python:*)\n---\nRun scripts/extract.py.\n");
        File.WriteAllText(Path.Combine(skill, "scripts", "extract.py"), "print('x')\n");
        RepoSkills.InvalidateCache();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var run = await h.Client.InvokeWithParameterObjectAsync<SlashCommandResult>(
            "command/slash", new { text = "/skill pdf summarize this file" }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var chip = Assert.Single(run.Effects!, e => e.Kind == "attachChip");
        Assert.Equal(Strings.SkillAttachmentLabel("pdf"), chip.Name);
        Assert.Contains("Run scripts/extract.py.", chip.Value);
        Assert.Equal("summarize this file", Assert.Single(run.Effects!, e => e.Kind == "sendAsPrompt").Value);
        Assert.True(run.Effects!.FindIndex(e => e.Kind == "attachChip") < run.Effects!.FindIndex(e => e.Kind == "sendAsPrompt"));

        var tools = h.Server.CurrentSession!.Tools;
        Assert.Contains(tools.Definitions, d => d.Function.Name == "read_skill_file");
        var before = h.Target.ApprovalPrompts;
        await tools.ExecuteAsync("run_command", JsonSerializer.SerializeToElement(
            new { command = "python " + Path.Combine(skill, "scripts", "extract.py") }), CancellationToken.None);
        Assert.Equal(before + 1, h.Target.ApprovalPrompts);
        Assert.Contains("extract.py", h.Target.LastApprovalMessage);
    }
}
