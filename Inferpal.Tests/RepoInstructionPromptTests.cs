using System.IO;
using Inferpal.Config;
using Inferpal.Services.Prompting;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The repository's own instructions to coding agents reach the model: one system-prompt section per source, after the
/// pinned files and before <c>.inferpal/</c> (which wins a conflict), deduplicated, inside the budget the prompt's files
/// share — a cut said, with its numbers.
/// </summary>
[Collection("Diagnostics")]
public sealed class RepoInstructionPromptTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"repo-prompt-{Guid.NewGuid():N}");

    public RepoInstructionPromptTests() => Directory.CreateDirectory(Path.Combine(_repo, ".git"));

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch { }
    }

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_repo, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private string P(string relative) => Path.Combine(_repo, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The prompt as a front-end builds it: the workspace root, its relative active file, and the file itself.</summary>
    private IReadOnlyList<PromptSection> Sections(string workspace, string? active, InferpalConfig? config = null,
                                                  int window = 0, IReadOnlySet<string>? disabled = null) =>
        new SystemPromptBuilder(config ?? new InferpalConfig(), contextWindow: window, workspaceRoot: workspace)
            .BuildSections("base", projectRoot: workspace,
                           activeFileRelPath: SystemPromptBuilder.RelativeActivePath(workspace, active),
                           disabledSectionIds: disabled, activeFilePath: active);

    private static List<PromptSection> Repo(IEnumerable<PromptSection> s) =>
        s.Where(x => x.Kind == PromptSectionKind.RepoInstructions).ToList();

    // ── The cahier's criteria ───────────────────────────────────────────────

    [Fact]
    public void AnAgentsFileAtTheGitRoot_ReachesAQuestionAskedFromASolutionInSrc()
    {
        Write("AGENTS.md", "AGENTS-ROOT: use xUnit.");
        var active = Write("src/App/Program.cs", "class P {}");

        var repo = Repo(Sections(P("src"), active));

        var section = Assert.Single(repo);
        Assert.Equal("AGENTS.md", section.Detail);
        Assert.Equal(P("AGENTS.md"), section.Key);
        Assert.Contains("AGENTS-ROOT: use xUnit.", section.Content);
    }

    [Fact]
    public void TheAgentsFilesDownToTheActiveFile_AreSentRootFirst_UnderOneHeader()
    {
        Write("AGENTS.md", "ROOT-RULES");
        Write("src/Api/AGENTS.md", "API-RULES");
        var active = Write("src/Api/X.cs", "class X {}");

        var repo = Repo(Sections(_repo, active));

        Assert.Equal(["AGENTS.md", "src/Api/AGENTS.md"], repo.Select(s => s.Detail));
        Assert.Equal(1, repo.Count(s => s.Content.Contains(ModelPrompts.RepoInstructionsHeading)));   // once, first
        Assert.Contains(ModelPrompts.RepoInstructionsHeading, repo[0].Content);
    }

    [Fact]
    public void AnActiveFileOutsideTheVisualStudioRoot_StillScopesTheChain()
    {
        // Visual Studio's root is the solution's folder; a test project beside it is outside, and its AGENTS.md is the
        // closest to the file being edited.
        Write("AGENTS.md", "ROOT-RULES");
        Write("tests/AGENTS.md", "TEST-RULES");
        Write("src/App.sln", "");
        var active = Write("tests/T.cs", "class T {}");

        Assert.Equal(["AGENTS.md", "tests/AGENTS.md"], Repo(Sections(P("src"), active)).Select(s => s.Detail));
    }

    [Fact]
    public void ACopilotInstructionScopedToCs_IsSentOnlyWithACsFileActive()
    {
        Write(".github/instructions/cs.instructions.md", "---\napplyTo: \"**/*.cs\"\n---\nCS-ONLY: use records.\n");

        Assert.Contains(Repo(Sections(_repo, Write("src/A.cs", "class A {}"))), s => s.Content.Contains("CS-ONLY"));
        Assert.Empty(Repo(Sections(_repo, Write("docs/readme.md", "# x"))));        // reference arm
        Assert.Empty(Repo(Sections(_repo, null)));                                     // no active file
    }

    [Fact]
    public void AClaudeFileIdenticalToAgents_IsSentOnce_TheOtherNamedADuplicate()
    {
        Write("AGENTS.md", "Same  rules.\nTwo lines.");
        Write("CLAUDE.md", "Same rules.\n\nTwo   lines.\n");                         // equal but for the blanks

        Assert.Equal(["AGENTS.md"], Repo(Sections(_repo, null)).Select(s => s.Detail));

        var composed = RepoInstructionComposition.Compose(
            RepoInstructionReader.ReadAll(RepoInstructionDiscovery.Discover(_repo, null)), null);
        var dup = Assert.Single(composed.Left);
        Assert.Equal(RepoInstructionLeftReason.Duplicate, dup.Reason);
        Assert.Equal(P("AGENTS.md"), dup.DuplicateOf);
    }

    [Fact]
    public void AClaudeFileThatOnlyImportsAgents_IsADuplicate_NotASecondCopy()
    {
        Write("AGENTS.md", "THE-RULES");
        Write("CLAUDE.md", "@AGENTS.md\n");

        var repo = Repo(Sections(_repo, null));
        Assert.Single(repo);
        Assert.Equal(1, repo.Sum(s => CountOf(s.Content, "THE-RULES")));
    }

    [Fact]
    public void AnImportAlreadySentAsASource_IsNotCopiedAgain()
    {
        Write("AGENTS.md", "THE-RULES");
        Write("CLAUDE.md", "Claude-specific.\n@AGENTS.md\n");

        var repo = Repo(Sections(_repo, null));
        Assert.Equal(["AGENTS.md", "CLAUDE.md"], repo.Select(s => s.Detail));
        Assert.Equal(1, repo.Sum(s => CountOf(s.Content, "THE-RULES")));
    }

    [Fact]
    public void ASaturatedWindow_CutsEachSourceWithItsNumbers_AndKeepsTheProjectFiles()
    {
        Write("AGENTS.md", string.Join('\n', Enumerable.Range(0, 2000).Select(i => $"rule {i}: keep things tidy")));
        Write(".inferpal/context.md", "PROJECT-CONTEXT");

        var sections = Sections(_repo, null, window: 8192);

        var agents = Assert.Single(Repo(sections));
        Assert.Matches(@"AGENTS\.md truncated to \d+ characters out of \d+", agents.Content);
        // Inside the budget the files share (one character per token of the window), not merely under a ceiling.
        var files = sections.Where(s => s.Kind is PromptSectionKind.RepoInstructions or PromptSectionKind.ProjectContext);
        Assert.True(files.Sum(s => s.Content.Length) < 8192 + 1000, $"files take {files.Sum(s => s.Content.Length)} characters");
        Assert.StartsWith("\n\n" + ModelPrompts.RepoInstructionsHeading, agents.Content);   // the head is kept
        Assert.Contains(sections, s => s.Kind == PromptSectionKind.ProjectContext && s.Content.Contains("PROJECT-CONTEXT"));
    }

    // ── Order, the X-Ray switch, the reference arm ──────────────────────────

    [Fact]
    public void RepositoryInstructions_SitBetweenThePinnedFilesAndInferpalsOwn()
    {
        var pin = Write("notes/pinned.md", "PINNED");
        Write("AGENTS.md", "REPO");
        Write(".inferpal/context.md", "CONTEXT");

        var kinds = Sections(_repo, null, new InferpalConfig { PinnedContextFiles = pin }).Select(s => s.Kind).ToList();

        Assert.True(kinds.IndexOf(PromptSectionKind.Pinned) < kinds.IndexOf(PromptSectionKind.RepoInstructions));
        Assert.True(kinds.IndexOf(PromptSectionKind.RepoInstructions) < kinds.IndexOf(PromptSectionKind.ProjectContext));
    }

    [Fact]
    public void ASourceSwitchedOffInTheXRay_HandsTheHeaderToTheNextOne()
    {
        Write("AGENTS.md", "ROOT-RULES");
        Write("CLAUDE.md", "CLAUDE-RULES");
        var off = new HashSet<string> { $"{PromptSectionKind.RepoInstructions}|{P("AGENTS.md")}" };

        var repo = Repo(Sections(_repo, null, disabled: off));

        var claude = repo.Single(s => s.Detail == "CLAUDE.md");
        Assert.Contains(ModelPrompts.RepoInstructionsHeading, claude.Content);
    }

    [Fact]
    public void ARepositoryWithoutInstructions_KeepsItsPromptAsItWas()
    {
        Write(".inferpal/context.md", "CONTEXT");
        var before = new SystemPromptBuilder(new InferpalConfig(), workspaceRoot: _repo).Build("base", projectRoot: _repo);
        var after  = string.Concat(Sections(_repo, Write("src/A.cs", "class A {}")).Select(s => s.Content));
        Assert.Equal(before, after);
    }

    // ── The composition alone ───────────────────────────────────────────────

    [Fact]
    public void WhatIsNotSent_IsSaidWithItsReason()
    {
        Write(".github/instructions/py.instructions.md", "---\ndescription: For Python tests\n---\nPY\n");
        Write(".cursor/rules/manual.mdc", "---\nalwaysApply: false\n---\nMANUAL\n");
        Write(".clinerules/off.md", "---\npaths: []\n---\nOFF\n");
        Write(".claude/rules/ts.md", "---\npaths: \"**/*.ts\"\n---\nTS\n");

        var composed = RepoInstructionComposition.Compose(
            RepoInstructionReader.ReadAll(RepoInstructionDiscovery.Discover(_repo, null)), "src/A.cs");

        Assert.Empty(composed.Sent);
        Assert.Equal(
            [RepoInstructionLeftReason.OnDemand, RepoInstructionLeftReason.NotThisFile,
             RepoInstructionLeftReason.Manual, RepoInstructionLeftReason.Never],
            composed.Left.Select(l => l.Reason));
    }

    private static int CountOf(string text, string what) =>
        (text.Length - text.Replace(what, string.Empty).Length) / what.Length;
}
