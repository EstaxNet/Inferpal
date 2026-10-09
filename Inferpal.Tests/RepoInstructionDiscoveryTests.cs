using System.IO;
using Inferpal.Services.Prompting;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Where a repository's own instructions to coding agents are found — AGENTS.md, Copilot's, Claude Code's, Cursor's,
/// Cline's, Roo's, Continue's — and what the discovery could not see, said with the result.
/// </summary>
public sealed class RepoInstructionDiscoveryTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"repo-instr-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"repo-instr-out-{Guid.NewGuid():N}");

    public RepoInstructionDiscoveryTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch { }
        try { Directory.Delete(_outside, recursive: true); } catch { }
    }

    private string Write(string relative, string text = "Use xUnit.\n")
    {
        var path = Path.Combine(_repo, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static List<string> Found(RepoInstructionDiscovery d) => d.Sources.Select(s => s.Path).ToList();

    // ── The cahier's acceptance criteria 1 and 2 ────────────────────────────

    [Fact]
    public void AnAgentsFileAtTheGitRoot_IsFound_FromASolutionInSrc()
    {
        // Visual Studio's workspace root is the folder that holds the solution, usually below the repository's root —
        // where AGENTS.md and .github/ live. Searching the workspace root alone misses the commonest .NET layout.
        var agents = Write("AGENTS.md");
        var active = Write("src/App/Program.cs", "class P {}");

        var d = RepoInstructionDiscovery.Discover(Path.Combine(_repo, "src"), active);

        Assert.Equal(_repo, d.SearchRoot);
        Assert.Equal([agents], Found(d));
    }

    [Fact]
    public void TheAgentsFilesOnTheActiveFilesPath_AreAllFound_RootFirst_AndNoOther()
    {
        var root  = Write("AGENTS.md");
        var api   = Write("src/Api/AGENTS.md");
        Write("src/Web/AGENTS.md");                                   // another branch: not on the path
        var active = Write("src/Api/X.cs", "class X {}");

        Assert.Equal([root, api], Found(RepoInstructionDiscovery.Discover(_repo, active)));
        // Reference arm: without an active file, the root's alone.
        Assert.Equal([root], Found(RepoInstructionDiscovery.Discover(_repo, activeFile: null)));
    }

    // ── The search root and its bounds ──────────────────────────────────────

    [Fact]
    public void AWorkTreeWhoseDotGitIsAFile_IsTheSearchRoot()
    {
        // A worktree (`git worktree add`) or a submodule: .git is a FILE.
        var wt = Path.Combine(_outside, "wt");
        Directory.CreateDirectory(Path.Combine(wt, "src"));
        File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: elsewhere\n");
        File.WriteAllText(Path.Combine(wt, "AGENTS.md"), "x");

        Assert.Equal(wt, RepoInstructionDiscovery.Discover(Path.Combine(wt, "src"), null).SearchRoot);
    }

    [Theory]
    [InlineData(@"C:\Users\me\dev\app\src", @"C:\Users\me\dev\app", @"C:\Users\me", @"C:\Users\me\dev\app")]   // ordinary
    [InlineData(@"C:\Users\me\dev\app\src", @"C:\Users\me", @"C:\Users\me", @"C:\Users\me\dev\app\src")]       // dotfiles repo at ~
    [InlineData(@"C:\Users\me\dev\app\src", @"C:\Users", @"C:\Users\me", @"C:\Users\me\dev\app\src")]          // above ~
    [InlineData(@"D:\work\app", @"D:\", @"C:\Users\me", @"D:\work\app")]                                        // a drive root
    [InlineData(@"D:\work\app", null, @"C:\Users\me", @"D:\work\app")]                                          // no repository
    public void TheSearchRoot_ClimbsToTheRepository_NeverToTheHomeFolderNorADriveRoot(
        string workspace, string? workTree, string home, string expected)
    {
        if (!OperatingSystem.IsWindows()) return;   // Windows paths: the rule itself is platform-free
        Assert.Equal(expected, RepoInstructionDiscovery.SearchRootFor(workspace, workTree, home));
    }

    // ── Exclusions ──────────────────────────────────────────────────────────

    [Fact]
    public void TheChain_NeverEntersADependencyOrBuildFolder()
    {
        // A package's own AGENTS.md is the package's instructions, not the repository's.
        var root = Write("AGENTS.md");
        Write("node_modules/left-pad/AGENTS.md");
        var active = Write("node_modules/left-pad/index.js", "module.exports = 1;");

        Assert.Equal([root], Found(RepoInstructionDiscovery.Discover(_repo, active)));
    }

    [Fact]
    public void ARulesFolderWrittenByHand_IsReadWhole_ASubfolderNamedBuildIncluded()
    {
        // `build/` is a topic in a rules folder, not build output: excluded, the rule would vanish in silence.
        var rule = Write(".claude/rules/build/ci.md");
        Assert.Equal([rule], Found(RepoInstructionDiscovery.Discover(_repo, null)));
    }

    // ── Every row of the table ──────────────────────────────────────────────

    [Fact]
    public void EveryFormatOfTheTable_IsFound_InTheTablesOrder()
    {
        var expected = new[]
        {
            Write("AGENTS.md"),
            Write(".github/copilot-instructions.md"),
            Write(".github/instructions/backend/api.instructions.md"),
            Write("CLAUDE.md"),
            Write(".claude/CLAUDE.md"),
            Write("CLAUDE.local.md"),
            Write(".claude/rules/tests.md"),
            Write(".cursor/rules/frontend/components.mdc"),
            Write(".cursorrules"),
            Write(".cline/rules/style.md"),
            Write(".roo/rules/naming.txt"),
            Write(".continue/rules/logging.md"),
        };
        Write(".github/instructions/notes.md");          // not *.instructions.md
        Write(".cursor/rules/readme.md");                 // Cursor reads .mdc only there
        Write(".continue/rules/nested/deep.md");          // Continue's folder is not recursive
        Write(".roo/rules/old.log");                      // Roo skips logs, backups, locks…

        var found = Found(RepoInstructionDiscovery.Discover(_repo, null));

        Assert.Equal(expected, found);
        // Each source carries its row: what it becomes, and the key that scopes it.
        var d = RepoInstructionDiscovery.Discover(_repo, null);
        Assert.Equal("applyTo", d.Sources.Single(s => s.Path.EndsWith("api.instructions.md")).Format.ScopeKey);
        Assert.Equal(RepoInstructionRole.Context, d.Sources.First().Format.Role);
    }

    [Fact]
    public void Clinerules_IsReadAsAFile_OrAsAFolderOfMarkdownAndText_NotItsWorkflows()
    {
        var file = Write(".clinerules");
        Assert.Equal([file], Found(RepoInstructionDiscovery.Discover(_repo, null)));

        File.Delete(file);
        var md  = Write(".clinerules/01-style.md");
        var mk  = Write(".clinerules/02-tests.markdown");
        var txt = Write(".clinerules/03-notes.txt");
        Write(".clinerules/settings.json");
        Write(".clinerules/workflows/release.md");        // a workflow, not a rule (and the folder is not recursive)

        Assert.Equal([md, mk, txt], Found(RepoInstructionDiscovery.Discover(_repo, null)));
    }

    [Fact]
    public void Roorules_IsTheFallback_OnlyWithoutRulesInRooRules()
    {
        var fallback = Write(".roorules");
        Assert.Equal([fallback], Found(RepoInstructionDiscovery.Discover(_repo, null)));

        Directory.CreateDirectory(Path.Combine(_repo, ".roo", "rules"));           // an empty folder changes nothing
        Assert.Equal([fallback], Found(RepoInstructionDiscovery.Discover(_repo, null)));

        var rule = Write(".roo/rules/naming.md");
        Assert.Equal([rule], Found(RepoInstructionDiscovery.Discover(_repo, null)));
    }

    [Fact]
    public void ARepositoryWithoutInstructions_FindsNothing_AndSaysNothing()
    {
        // Reference arm: an ordinary repository must not grow notes nobody can act on.
        Write("README.md");
        Write("src/A.cs", "class A {}");

        var d = RepoInstructionDiscovery.Discover(_repo, Path.Combine(_repo, "src", "A.cs"));
        Assert.Empty(d.Sources);
        Assert.Empty(d.Unseen);
    }

    [Fact]
    public void NoWorkspace_NoDiscovery()
    {
        var d = RepoInstructionDiscovery.Discover(null, Write("AGENTS.md"));
        Assert.Null(d.SearchRoot);
        Assert.Empty(d.Sources);
    }

    // ── What it could not see is said ───────────────────────────────────────

    [Fact]
    public void ALinkWhoseTargetLeavesTheRepository_IsNotRead_AndIsSaid()
    {
        // A repository-authored link to a file outside it would put that file in front of the model.
        var secret = Path.Combine(_outside, "secret.txt");
        File.WriteAllText(secret, "token=abc");
        var link = Path.Combine(_repo, "AGENTS.md");
        try { File.CreateSymbolicLink(link, secret); } catch { }
        Assert.True(File.Exists(link), "The link could not be created (symbolic-link privilege): this test is UNDECIDED, not green.");
        var inside = Write("docs/AGENTS-real.md");
        var claude = Path.Combine(_repo, "CLAUDE.md");
        File.CreateSymbolicLink(claude, inside);                                    // a link that stays inside is read

        var d = RepoInstructionDiscovery.Discover(_repo, null);

        Assert.Equal([claude], Found(d));
        Assert.Contains(d.Unseen, u => u.Path == link && u.Reason == RepoInstructionUnseenReason.LinkLeavesTheRepository);
    }

    [Fact]
    public void AFolderWithMoreRulesThanTheCap_KeepsTheFirstInOrdinalOrder_AndSaysHowManyWereLeft()
    {
        var all = Enumerable.Range(0, RepoInstructionDiscovery.MaxFilesPerFormat + 7)
                            .Select(i => Write($".claude/rules/r{i:D3}.md"))
                            .OrderBy(p => p, StringComparer.Ordinal).ToList();

        var d = RepoInstructionDiscovery.Discover(_repo, null);

        Assert.Equal(all.Take(RepoInstructionDiscovery.MaxFilesPerFormat), Found(d));
        var capped = Assert.Single(d.Unseen);
        Assert.Equal(RepoInstructionUnseenReason.Capped, capped.Reason);
        Assert.Equal(7, capped.Count);
    }
}
