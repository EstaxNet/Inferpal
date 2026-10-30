using System.IO;
using System.Text;
using Inferpal.Services.Prompting;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What each repository instruction file says, and when it applies: the front matter of each tool read as that tool
/// reads it, CLAUDE.md's imports bounded to the repository, and what could not be read said as data.
/// </summary>
public sealed class RepoInstructionReaderTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"repo-read-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"repo-read-out-{Guid.NewGuid():N}");

    public RepoInstructionReaderTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch { }
        try { Directory.Delete(_outside, recursive: true); } catch { }
    }

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_repo, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Discovers then reads the one source at <paramref name="relative"/>.</summary>
    private RepoInstruction Read(string relative)
    {
        var path = Path.Combine(_repo, relative.Replace('/', Path.DirectorySeparatorChar));
        var all = RepoInstructionReader.ReadAll(RepoInstructionDiscovery.Discover(_repo, null));
        return all.Single(i => i.Source.Path == path);
    }

    // ── Formats without a scope key: always, the whole text ─────────────────

    [Fact]
    public void AnAgentsFile_AppliesAlways_AsWritten()
    {
        Write("AGENTS.md", "# Conventions\nUse xUnit.\n");
        var i = Read("AGENTS.md");
        Assert.Equal(RepoInstructionScope.Always, i.Scope);
        Assert.Equal("# Conventions\nUse xUnit.", i.Body);
        Assert.True(i.AppliesTo(null));
    }

    // ── A symbolic link cloned as a one-line file ───────────────────────────

    /// <summary>Discovers, then reads the sources <paramref name="include"/> takes, and returns the one at
    /// <paramref name="relative"/>.</summary>
    private RepoInstruction ReadTaking(string relative, Func<RepoInstructionSource, bool> include)
    {
        var path = Path.Combine(_repo, relative.Replace('/', Path.DirectorySeparatorChar));
        return RepoInstructionReader.ReadAll(RepoInstructionDiscovery.Discover(_repo, null), include).Single(i => i.Source.Path == path);
    }

    private static bool ClaudeOnly(RepoInstructionSource s) => s.Format.Family == RepoInstructionFamily.Claude;

    private static bool AgentsOnly(RepoInstructionSource s) => s.Format.Family == RepoInstructionFamily.Agents;

    /// <summary>CLAUDE.md → AGENTS.md cloned without links: AGENTS.md is read once, and CLAUDE.md adds nothing — never
    /// "AGENTS.md" handed to the model as the repository's instructions.</summary>
    [Fact]
    public void ALinkToAFileReadOnItsOwn_AddsNothing()
    {
        Write("AGENTS.md", "Use xUnit.\n");
        Write("CLAUDE.md", "AGENTS.md");

        Assert.Equal(string.Empty, Read("CLAUDE.md").Body);
        Assert.Equal("Use xUnit.", Read("AGENTS.md").Body);
    }

    /// <summary>The same link with the AGENTS family turned off: AGENTS.md is read through CLAUDE.md.</summary>
    [Fact]
    public void ALinkToAnInstructionFileLeftOut_IsReadThrough()
    {
        Write("AGENTS.md", "Use xUnit.\n");
        Write("CLAUDE.md", "AGENTS.md");

        Assert.Equal("Use xUnit.", ReadTaking("CLAUDE.md", ClaudeOnly).Body);
    }

    /// <summary>A link whose target links again is followed to the text — never the second link's one line.</summary>
    [Fact]
    public void AChainOfLinks_IsFollowedToTheText()
    {
        Write(".github/copilot-instructions.md", "Keep methods short.\n");
        Write("AGENTS.md", ".github/copilot-instructions.md");
        Write("CLAUDE.md", "AGENTS.md");

        Assert.Equal("Keep methods short.", ReadTaking("CLAUDE.md", ClaudeOnly).Body);
    }

    /// <summary>A target that is not an instruction file is not followed: read through, it would become the system prompt
    /// while a write to it does not force the approval card. Said, and its one line never sent.</summary>
    [Fact]
    public void ALinkToAFileThatIsNotAnInstructionFile_IsNotFollowed_AndSaysSo()
    {
        Write("docs/ai.md", "Run the tests with dotnet test.\n");
        Write("AGENTS.md", "docs/ai.md");

        var i = Read("AGENTS.md");
        Assert.Equal(string.Empty, i.Body);
        Assert.Contains(i.Notes, n => n.Kind == RepoInstructionNoteKind.LinkNotFollowed);
    }

    /// <summary>A broken link (its target missing, a submodule not checked out) sends nothing either.</summary>
    [Fact]
    public void ABrokenLink_SendsNothing_AndSaysSo()
    {
        Write("AGENTS.md", "docs/missing.md");

        var i = Read("AGENTS.md");
        Assert.Equal(string.Empty, i.Body);
        Assert.Contains(i.Notes, n => n.Kind == RepoInstructionNoteKind.LinkNotFollowed);
    }

    [Fact]
    public void ALinkLeavingTheRepository_ReadsNothing_AndSaysSo()
    {
        File.WriteAllText(Path.Combine(_outside, "AGENTS.md"), "never read\n");
        var relative = Path.GetRelativePath(_repo, Path.Combine(_outside, "AGENTS.md")).Replace('\\', '/');
        Write("CLAUDE.md", relative);

        var i = ReadTaking("CLAUDE.md", ClaudeOnly);
        Assert.Equal(string.Empty, i.Body);
        Assert.Contains(i.Notes, n => n.Kind == RepoInstructionNoteKind.LinkNotFollowed);
    }

    /// <summary>A link leads only to an instruction file, which the ignore files never exclude (instructions are exempt,
    /// as when the file is found directly): read through as it would be read, and nothing else is reachable that way —
    /// an excluded file that is not an instruction file is not followed at all.</summary>
    [Fact]
    public void ALinkReachesOnlyInstructionFiles_WhichTheIgnoreFilesExemptAsUsual()
    {
        Write(".aiignore", "private/\n");
        Write("private/CLAUDE.md", "Use the staging database.\n");
        Write("private/notes.md", "the client's names\n");
        Write("AGENTS.md", "private/CLAUDE.md");
        Write("CLAUDE.md", "private/notes.md");
        Inferpal.Services.Governance.ContentExclusions.Declare(_repo, () => Inferpal.Services.Governance.ContentExclusions.DefaultFiles);
        try
        {
            Assert.Equal("Use the staging database.", ReadTaking("AGENTS.md", AgentsOnly).Body);
            var notFollowed = ReadTaking("CLAUDE.md", ClaudeOnly);
            Assert.Equal(string.Empty, notFollowed.Body);
            Assert.Contains(notFollowed.Notes, n => n.Kind == RepoInstructionNoteKind.LinkNotFollowed);
        }
        finally { Inferpal.Services.Governance.ContentExclusions.ForgetForTests(_repo); }
    }

    /// <summary>A CLAUDE.md read through its link imports relative to the target, as the file would if it were there.</summary>
    [Fact]
    public void ALinkedClaudeFile_ImportsRelativeToItsTarget()
    {
        Write("notes/style.md", "Tabs, not spaces.\n");
        Write("notes/CLAUDE.md", "See @./style.md\n");
        Write("CLAUDE.md", "notes/CLAUDE.md");
        var root = Path.Combine(_repo, "CLAUDE.md");

        var i = ReadTaking("CLAUDE.md", s => s.Path == root);
        Assert.Contains(i.Imports, imp => imp.Text.Contains("Tabs, not spaces.", StringComparison.Ordinal));
    }

    /// <summary>The page that lists the instructions says a link to another source is that source — never "could not
    /// be read".</summary>
    [Fact]
    public void ALinkToAnotherSource_IsListedAsItsDuplicate()
    {
        Write("AGENTS.md", "Use xUnit.\n");
        Write("CLAUDE.md", "AGENTS.md");
        var read = RepoInstructionReader.ReadAll(RepoInstructionDiscovery.Discover(_repo, null));

        var left = Assert.Single(RepoInstructionComposition.Compose(read, null).Left,
                                 l => l.Instruction.Source.Path == Path.Combine(_repo, "CLAUDE.md"));
        Assert.Equal(RepoInstructionLeftReason.Duplicate, left.Reason);
        Assert.Equal(Path.Combine(_repo, "AGENTS.md"), left.DuplicateOf);
    }

    // ── Copilot: applyTo ────────────────────────────────────────────────────

    [Fact]
    public void ACopilotInstruction_AppliesToItsApplyToGlobs_Only()
    {
        // The cahier's criterion 3, and GitHub's own example of several comma-separated globs.
        Write(".github/instructions/cs.instructions.md", "---\napplyTo: \"**/*.cs\"\n---\nUse records.\n");
        Write(".github/instructions/ts.instructions.md", "---\napplyTo: \"**/*.ts,**/*.tsx\"\n---\nNo any.\n");

        var cs = Read(".github/instructions/cs.instructions.md");
        Assert.Equal(RepoInstructionScope.Files, cs.Scope);
        Assert.Equal("Use records.", cs.Body);
        Assert.True(cs.AppliesTo("src/App/Program.cs"));
        Assert.False(cs.AppliesTo("src/web/app.ts"));
        Assert.False(cs.AppliesTo(null));                              // no active file: a scoped rule waits

        var ts = Read(".github/instructions/ts.instructions.md");
        Assert.Equal(["**/*.ts", "**/*.tsx"], ts.Globs);
        Assert.True(ts.AppliesTo("web/src/view.tsx"));
    }

    [Fact]
    public void ACopilotInstructionWithoutApplyTo_IsOnDemandByItsDescription_OrManual()
    {
        Write(".github/instructions/py.instructions.md", "---\ndescription: 'Use when writing Python tests.'\n---\nUse pytest.\n");
        Write(".github/instructions/bare.instructions.md", "Attach me by hand.\n");

        var described = Read(".github/instructions/py.instructions.md");
        Assert.Equal(RepoInstructionScope.OnDemand, described.Scope);
        Assert.Equal("Use when writing Python tests.", described.Description);
        Assert.False(described.AppliesTo("tests/test_a.py"));

        Assert.Equal(RepoInstructionScope.Manual, Read(".github/instructions/bare.instructions.md").Scope);
    }

    // ── Claude Code and Cline: paths ────────────────────────────────────────

    [Theory]
    [InlineData("---\npaths:\n  - \"src/**/*.ts\"\n  - \"lib/**\"\n---\nRule.\n")]   // a YAML list
    [InlineData("---\npaths: src/**/*.ts, lib/**\n---\nRule.\n")]                      // a comma-separated string
    [InlineData("---\npaths: [\"src/**/*.ts\", \"lib/**\"]\n---\nRule.\n")]           // an inline array
    public void AClaudeRule_AppliesToItsPaths_WrittenAnyOfTheThreeWays(string text)
    {
        Write(".claude/rules/api.md", text);
        var i = Read(".claude/rules/api.md");
        Assert.Equal(RepoInstructionScope.Files, i.Scope);
        Assert.Equal(["src/**/*.ts", "lib/**"], i.Globs);
        Assert.Equal("Rule.", i.Body);
    }

    [Fact]
    public void AClaudeRuleWithoutPaths_AppliesAlways()
    {
        Write(".claude/rules/style.md", "Keep functions short.\n");
        Assert.Equal(RepoInstructionScope.Always, Read(".claude/rules/style.md").Scope);
    }

    [Fact]
    public void AnEmptyPathsList_NeverApplies_AndSaysSo()
    {
        // Cline reads `paths: []` as "never" (fail-closed): a rule written that way is switched off — said, not silent.
        Write(".clinerules/off.md", "---\npaths: []\n---\nOld rule.\n");
        var i = Read(".clinerules/off.md");
        Assert.Equal(RepoInstructionScope.Never, i.Scope);
        Assert.False(i.AppliesTo("src/a.ts"));
        Assert.Contains(i.Notes, n => n.Kind == RepoInstructionNoteKind.NeverApplies);
    }

    // ── Cursor: the type is deduced from three keys ─────────────────────────

    [Theory]
    [InlineData("---\nalwaysApply: true\nglobs: src/**\n---\nR.\n", "Always")]
    [InlineData("---\nglobs: src/components/**/*.tsx\nalwaysApply: false\n---\nR.\n", "Files")]
    [InlineData("---\ndescription: When touching the API\nalwaysApply: false\n---\nR.\n", "OnDemand")]
    [InlineData("---\nalwaysApply: false\n---\nR.\n", "Manual")]
    public void ACursorRule_TakesTheTypeItsFrontMatterDescribes(string text, string expected)
    {
        Write(".cursor/rules/r.mdc", text);
        var i = Read(".cursor/rules/r.mdc");
        Assert.Equal(Enum.Parse<RepoInstructionScope>(expected), i.Scope);
        Assert.Equal("R.", i.Body);
    }

    // ── Continue ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("---\nalwaysApply: true\nglobs: '**/*.ts'\n---\nR.\n", "Always")]
    [InlineData("---\nglobs: '**/*.ts'\n---\nR.\n", "Files")]
    [InlineData("---\nname: plain\n---\nR.\n", "Always")]                         // no globs: always
    [InlineData("---\nalwaysApply: false\ndescription: For migrations\n---\nR.\n", "OnDemand")]
    public void AContinueRule_FollowsContinuesRules(string text, string expected)
    {
        Write(".continue/rules/r.md", text);
        Assert.Equal(Enum.Parse<RepoInstructionScope>(expected), Read(".continue/rules/r.md").Scope);
    }

    [Fact]
    public void AContinueRegex_IsNamedAsNotApplied()
    {
        Write(".continue/rules/r.md", "---\nglobs: '**/*.ts'\nregex: 'useEffect'\n---\nR.\n");
        Assert.Contains(Read(".continue/rules/r.md").Notes,
                        n => n.Kind == RepoInstructionNoteKind.KeyNotApplied && n.Subject == "regex");
    }

    // ── CLAUDE.md imports ───────────────────────────────────────────────────

    [Fact]
    public void AClaudeImport_IsRead_RelativeToTheFileThatImportsIt()
    {
        Write("CLAUDE.md", "See @docs/style.md for the style.\n");
        Write("docs/style.md", "Tabs, not spaces. Also @rules/naming.md\n");
        Write("docs/rules/naming.md", "PascalCase.\n");

        var i = Read("CLAUDE.md");

        Assert.Equal("See @docs/style.md for the style.", i.Body);                  // the reference stays where it was
        Assert.Equal([Path.Combine(_repo, "docs", "style.md"), Path.Combine(_repo, "docs", "rules", "naming.md")],
                     i.Imports.Select(m => m.Path));
        Assert.Equal("PascalCase.", i.Imports[1].Text);
    }

    [Fact]
    public void Imports_StopAtFourHops_AndSaySo()
    {
        Write("CLAUDE.md", "@h1.md\n");
        for (var hop = 1; hop <= 5; hop++) Write($"h{hop}.md", hop < 5 ? $"@h{hop + 1}.md\n" : "too deep\n");

        var i = Read("CLAUDE.md");

        Assert.Equal(4, i.Imports.Count);
        Assert.Contains(i.Notes, n => n.Kind == RepoInstructionNoteKind.ImportTooDeep
                                      && n.Subject == Path.Combine(_repo, "h5.md"));
    }

    [Fact]
    public void WhatIsNotAnImport_IsNotImported()
    {
        Write("CLAUDE.md",
            "Mail dev@team.md or ask @nobody.\n"                  // an e-mail; a mention that is no file
            + "Run `@docs/style.md` in code.\n"                  // a code span
            + "```\n@docs/style.md\n```\n"                        // a code block
            + "Quoted: \"@docs/style.md\"\n");                    // quoted
        Write("docs/style.md", "Tabs.\n");
        Write("team.md", "x\n");

        var i = Read("CLAUDE.md");
        Assert.Empty(i.Imports);
        Assert.Empty(i.Notes);                                     // a mention that resolves to nothing says nothing
    }

    [Fact]
    public void AnImportOutsideTheRepository_IsNotRead_AndIsSaid()
    {
        var secret = Path.Combine(_outside, "secret.md");
        File.WriteAllText(secret, "token=abc\n");
        Write("CLAUDE.md", $"@{secret.Replace('\\', '/')}\n@../{Path.GetFileName(_outside)}/secret.md\n");

        var i = Read("CLAUDE.md");

        Assert.Empty(i.Imports);
        Assert.Contains(i.Notes, n => n.Kind == RepoInstructionNoteKind.ImportOutsideTheRepository);
        Assert.DoesNotContain("token", string.Concat(i.Imports.Select(m => m.Text)));
    }

    [Fact]
    public void AnImportCycle_ReadsEachFileOnce()
    {
        Write("CLAUDE.md", "@a.md\n");
        Write("a.md", "@b.md\n");
        Write("b.md", "@a.md and @CLAUDE.md\n");

        var i = Read("CLAUDE.md");
        Assert.Equal([Path.Combine(_repo, "a.md"), Path.Combine(_repo, "b.md")], i.Imports.Select(m => m.Path));
    }

    [Fact]
    public void HtmlComments_AreDroppedFromClaudeMd_OutsideCode()
    {
        Write("CLAUDE.md", "Keep <!-- the maintainers' note --> this.\n<!--\nmulti\n-->\n```html\n<!-- kept -->\n```\n");
        var i = Read("CLAUDE.md");
        Assert.DoesNotContain("maintainers", i.Body);
        Assert.DoesNotContain("multi", i.Body);
        Assert.Contains("<!-- kept -->", i.Body);
    }

    // ── What cannot be read ─────────────────────────────────────────────────

    [Fact]
    public void ABinaryRuleFile_IsNotRead_AndIsSaid()
    {
        var path = Path.Combine(_repo, ".roo", "rules", "logo.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x00, 0x00, 0x01]);

        var i = Read(".roo/rules/logo.png");
        Assert.Equal(string.Empty, i.Body);
        Assert.False(i.AppliesTo(null));
        Assert.Contains(i.Notes, n => n.Kind == RepoInstructionNoteKind.Binary);
    }

    [Fact]
    public void AFileTooLargeToBeAnInstruction_IsNotRead_AndIsSaid()
    {
        Write("AGENTS.md", new string('x', RepoInstructionReader.MaxFileBytes + 1));
        var i = Read("AGENTS.md");
        Assert.Equal(string.Empty, i.Body);
        Assert.Contains(i.Notes, n => n.Kind == RepoInstructionNoteKind.TooLarge);
    }

    [Fact]
    public void AFileInALegacyCodePage_IsReadInIt()
    {
        if (!OperatingSystem.IsWindows()) return;   // the system's ANSI code page is what decides
        var path = Path.Combine(_repo, "AGENTS.md");
        File.WriteAllBytes(path, [.. Encoding.ASCII.GetBytes("Caf"), 0xE9, .. Encoding.ASCII.GetBytes(" au lait.\n")]);
        Assert.Equal("Café au lait.", Read("AGENTS.md").Body);
    }
}
