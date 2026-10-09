using System.IO;
using Inferpal.Config;
using Inferpal.Host;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Presentation;
using Inferpal.Services.Prompting;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Where the repository's instructions show: one row per file found — its tool, when it applies, what the next
/// question sends of it, and why not when it does not — read by the Context page of both editors and by
/// <c>/instructions</c>. The rows come from the prompt's own path and its X-Ray, so they show what is sent.
/// </summary>
[Collection("Diagnostics")]
public sealed class RepoInstructionsReportTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"repo-report-{Guid.NewGuid():N}");

    public RepoInstructionsReportTests() => Directory.CreateDirectory(Path.Combine(_repo, ".git"));

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch { }
    }

    private string Write(string relative, string text)
    {
        var path = P(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private string P(string relative) => Path.Combine(_repo, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The rows as a front-end builds them: the plan and the X-Ray of the same prompt, same inputs.</summary>
    private IReadOnlyList<RepoInstructionRow> Rows(string? active = null, InferpalConfig? config = null, int window = 0,
                                                    IReadOnlySet<string>? disabled = null)
    {
        config ??= new InferpalConfig();
        var builder  = new SystemPromptBuilder(config, contextWindow: window, workspaceRoot: _repo);
        var relative = SystemPromptBuilder.RelativeActivePath(_repo, active);
        var sections = builder.BuildSections("base", projectRoot: _repo, activeFileRelPath: relative,
                                             disabledSectionIds: disabled, activeFilePath: active);
        var xray     = XRayPanelPresenter.Build(sections, disabled, 0, window);
        return RepoInstructionsReport.Rows(builder.RepoInstructions(_repo, relative, active), xray);
    }

    private static RepoInstructionRow Row(IReadOnlyList<RepoInstructionRow> rows, string file) =>
        Assert.Single(rows, r => r.File == file);

    // ── What is sent, and how much ──────────────────────────────────────────

    [Fact]
    public void ASentFile_ShowsTheXRaysWeight_NotTheFilesSize()
    {
        Write("AGENTS.md", "Use xUnit. Keep methods short.");
        var config   = new InferpalConfig();
        var sections = new SystemPromptBuilder(config, workspaceRoot: _repo).BuildSections("base", projectRoot: _repo);
        var section  = XRayPanelPresenter.Build(sections, null, 0, 0).Sections
                           .Single(s => s.Id == $"{PromptSectionKind.RepoInstructions}|{P("AGENTS.md")}");

        var row = Row(Rows(config: config), "AGENTS.md");

        Assert.True(row.IsSent);
        Assert.Equal("AGENTS.md", row.Family);
        Assert.Equal(Strings.RepoInstructionScopeAlways, row.Scope);
        Assert.Equal(Strings.RepoInstructionStateSent, row.State);
        // The section carries the header too: its weight, not the file's, is what the question pays.
        Assert.Equal(Strings.PinnedFileTokens(SettingsWidgets.Amount(section.Tokens)), row.Sent);
    }

    [Fact]
    public void ACutFile_ShowsWhatIsSentOutOfTheWhole()
    {
        Write("AGENTS.md", string.Join('\n', Enumerable.Range(0, 2000).Select(i => $"rule {i}: keep things tidy")));

        var builder  = new SystemPromptBuilder(new InferpalConfig(), contextWindow: 8192, workspaceRoot: _repo);
        var section  = XRayPanelPresenter.Build(builder.BuildSections("base", projectRoot: _repo), null, 0, 8192).Sections
                           .Single(s => s.Id == $"{PromptSectionKind.RepoInstructions}|{P("AGENTS.md")}");
        var whole    = Services.Commands.XRayCommandHandler.EstimateTokens(builder.RepoInstructions(_repo)!.Composed.Sent[0].Text);
        Assert.True(section.Tokens < whole, $"{section.Tokens} of {whole}");     // WITNESS: the window does cut it

        var row = Row(Rows(window: 8192), "AGENTS.md");

        Assert.True(row.IsSent);
        Assert.Equal(Strings.PinnedFileTokensSent(SettingsWidgets.Amount(section.Tokens), SettingsWidgets.Amount(whole)), row.Sent);
        // The reference arm — a file that fits shows one figure — is ASentFile_ShowsTheXRaysWeight_NotTheFilesSize.
    }

    [Fact]
    public void AFileSwitchedOffInTheXRay_IsNotSent_AndSaysWhere()
    {
        Write("AGENTS.md", "ROOT-RULES");
        var off = new HashSet<string> { $"{PromptSectionKind.RepoInstructions}|{P("AGENTS.md")}" };

        var row = Row(Rows(disabled: off), "AGENTS.md");

        Assert.False(row.IsSent);
        Assert.Equal(Strings.RepoInstructionStateSwitchedOff, row.State);
        Assert.Equal("—", row.Sent);
    }

    // ── Why not ─────────────────────────────────────────────────────────────

    [Fact]
    public void EachFileLeftOut_SaysItsReason()
    {
        Write(".github/instructions/py.instructions.md", "---\ndescription: For Python tests\n---\nPY\n");
        Write(".cursor/rules/manual.mdc", "---\nalwaysApply: false\n---\nMANUAL\n");
        Write(".clinerules/off.md", "---\npaths: []\n---\nOFF\n");
        Write(".claude/rules/ts.md", "---\npaths: \"**/*.ts\"\n---\nTS\n");
        var active = Write("src/A.cs", "class A {}");

        var rows = Rows(active);

        Assert.Equal(Strings.RepoInstructionStateOnDemand,    Row(rows, ".github/instructions/py.instructions.md").State);
        Assert.Equal(Strings.RepoInstructionScopeOnDemand,    Row(rows, ".github/instructions/py.instructions.md").Scope);
        Assert.Equal(Strings.RepoInstructionStateManual,      Row(rows, ".cursor/rules/manual.mdc").State);
        Assert.Equal(Strings.RepoInstructionScopeManual,      Row(rows, ".cursor/rules/manual.mdc").Scope);
        Assert.Equal(Strings.RepoInstructionStateNever,       Row(rows, ".clinerules/off.md").State);
        Assert.Equal(Strings.RepoInstructionStateNotThisFile, Row(rows, ".claude/rules/ts.md").State);
        Assert.Equal(Strings.RepoInstructionScopeFiles("**/*.ts"), Row(rows, ".claude/rules/ts.md").Scope);
        Assert.All(rows, r => Assert.False(r.IsSent));
        Assert.All(rows, r => Assert.Equal("—", r.Sent));
    }

    [Fact]
    public void AScopedFile_IsSentWhenTheActiveFileMatches()
    {
        Write(".claude/rules/ts.md", "---\npaths: \"**/*.ts\"\n---\nTS\n");

        Assert.True(Row(Rows(Write("web/app.ts", "let x = 1;")), ".claude/rules/ts.md").IsSent);
        Assert.False(Row(Rows(Write("web/app.cs", "class X {}")), ".claude/rules/ts.md").IsSent);    // reference arm
    }

    [Fact]
    public void ADuplicate_NamesTheFileItRepeats()
    {
        Write("AGENTS.md", "Same rules.");
        Write("CLAUDE.md", "Same  rules.\n");

        var rows = Rows();

        Assert.True(Row(rows, "AGENTS.md").IsSent);
        Assert.Equal(Strings.RepoInstructionStateDuplicate("AGENTS.md"), Row(rows, "CLAUDE.md").State);
    }

    [Fact]
    public void AnUnreadableFile_SaysSo()
    {
        File.WriteAllBytes(P("AGENTS.md"), [0x41, 0x00, 0x42, 0x00, 0x00, 0x01, 0x02]);   // binary

        var row = Row(Rows(), "AGENTS.md");

        Assert.False(row.IsSent);
        Assert.Equal(Strings.RepoInstructionStateNotRead, row.State);
        Assert.Equal("—", row.Scope);
    }

    // ── The families the setting reads ──────────────────────────────────────

    [Fact]
    public void AFamilyTurnedOff_IsNotRead_NotSent_AndItsRowSaysWhy()
    {
        Write("AGENTS.md", "AGENTS-RULES");
        Write("CLAUDE.md", "CLAUDE-ONLY-RULES");
        var config = new InferpalConfig { RepoInstructionFamilies = "agents, copilot" };

        var rows = Rows(config: config);
        var claude = Row(rows, "CLAUDE.md");
        Assert.Equal(Strings.RepoInstructionStateFamilyOff, claude.State);
        Assert.Equal("Claude Code", claude.Family);
        Assert.False(claude.IsSent);
        Assert.True(Row(rows, "AGENTS.md").IsSent);

        // End to end: the prompt itself no longer carries it.
        var prompt = new SystemPromptBuilder(config, workspaceRoot: _repo).Build("base", projectRoot: _repo);
        Assert.DoesNotContain("CLAUDE-ONLY-RULES", prompt);
        Assert.Contains("AGENTS-RULES", prompt);
        // Reference arm: the default reads every family.
        Assert.Contains("CLAUDE-ONLY-RULES", new SystemPromptBuilder(new InferpalConfig(), workspaceRoot: _repo)
                                                 .Build("base", projectRoot: _repo));
    }

    [Fact]
    public void AnEmptySetting_ReadsNone()
    {
        Write("AGENTS.md", "AGENTS-RULES");
        var config = new InferpalConfig { RepoInstructionFamilies = "" };

        Assert.Equal(Strings.RepoInstructionStateFamilyOff, Row(Rows(config: config), "AGENTS.md").State);
        Assert.DoesNotContain("AGENTS-RULES", new SystemPromptBuilder(config, workspaceRoot: _repo).Build("base", projectRoot: _repo));
    }

    [Fact]
    public void AnUnknownFamilyName_IsSaidOnce_TheOthersStillRead()
    {
        Write("AGENTS.md", "AGENTS-RULES");
        Diagnostics.Clear();
        var config = new InferpalConfig { RepoInstructionFamilies = "agents, cursorr" };

        Rows(config: config);
        Rows(config: config);

        var notes = Diagnostics.Snapshot().Where(e => e.Detail.Contains("cursorr", StringComparison.Ordinal)).ToList();
        Assert.Single(notes);
        Assert.Contains("repoInstructionFamilies", notes[0].Detail + notes[0].Context, StringComparison.Ordinal);
        Assert.True(Row(Rows(config: config), "AGENTS.md").IsSent);
    }

    [Fact]
    public void TheDefaultSetting_NamesEveryFamily()
    {
        var (on, unknown) = RepoInstructionFormats.Families(new InferpalConfig().RepoInstructionFamilies);
        Assert.Empty(unknown);
        Assert.Equal(Enum.GetValues<RepoInstructionFamily>().Length, on.Count);
        // Every name the setting takes is one the hint lists — a name the hint leaves out cannot be found.
        Assert.All(RepoInstructionFormats.FamilyNames.Keys, name =>
            Assert.Contains(name, Strings.HintRepoInstructionFamilies, StringComparison.Ordinal));
    }

    // ── /instructions ────────────────────────────────────────────────────────

    [Fact]
    public void ARepositoryWithoutInstructions_IsOneSentence_NotAnEmptyTable()
    {
        Assert.Empty(Rows());
        Assert.Equal(Strings.RepoInstructionsNone, RepoInstructionsReport.Markdown(Rows()));
    }

    [Fact]
    public void TheTable_ListsEveryFileInTheOrderThePromptCarriesThem()
    {
        Write("AGENTS.md", "ROOT");
        Write("src/Api/AGENTS.md", "API");
        Write(".claude/rules/ts.md", "---\npaths: \"**/*.ts\"\n---\nTS\n");
        var rows = Rows(Write("src/Api/X.cs", "class X {}"));

        var md = RepoInstructionsReport.Markdown(rows);

        Assert.StartsWith("## " + Strings.RepoInstructionsTitle, md);
        Assert.Contains($"| {Strings.RepoInstructionsColFamily} | {Strings.RepoInstructionsColFile} |", md);
        Assert.True(md.IndexOf("`AGENTS.md`", StringComparison.Ordinal) < md.IndexOf("`src/Api/AGENTS.md`", StringComparison.Ordinal));
        Assert.Equal(3, md.Split('\n').Count(l => l.StartsWith("| ", StringComparison.Ordinal) && l.Contains('`')));
        Assert.Contains(Strings.RepoInstructionStateNotThisFile, md);
    }

    [Fact]
    public void ACellWithAPipe_DoesNotBreakTheTable()
    {
        var row = new RepoInstructionRow("Cursor", "a.mdc", "/x/a.mdc", Strings.RepoInstructionScopeFiles("a|b"), "—", "x", false);
        var line = RepoInstructionsReport.Markdown([row]).Split('\n').Last();
        // Five columns, six separators: the pipe in the cell is escaped, not one more column.
        Assert.Equal(6, System.Text.RegularExpressions.Regex.Matches(line, @"(?<!\\)\|").Count);
        Assert.Contains("a\\|b", line);
    }
}

public partial class HostServerTests
{
    /// <summary>
    /// VS Code: <c>/instructions</c> and the Context page's rows come from the host, built from the prompt the next
    /// question sends — the active file the editor reported included.
    /// </summary>
    [Fact]
    public async Task Instructions_ListTheRepositorysFiles_ScopedToTheActiveFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"host-instr-list-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        Directory.CreateDirectory(Path.Combine(root, ".github", "instructions"));
        File.WriteAllText(Path.Combine(root, "AGENTS.md"), "AGENTS-RULES");
        File.WriteAllText(Path.Combine(root, ".github", "instructions", "cs.instructions.md"),
            "---\napplyTo: \"**/*.cs\"\n---\nCS-RULES");
        try
        {
            using var h = CreateHarness();
            await h.InitializeAsync(rootDir: root).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            var before = await h.Client.InvokeAsync<List<RepoInstructionRow>>("settings/repoInstructions")
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.Equal(["AGENTS.md", ".github/instructions/cs.instructions.md"], before.Select(r => r.File));
            Assert.Equal(Strings.RepoInstructionStateNotThisFile, before[1].State);

            await h.Client.NotifyWithParameterObjectAsync("editor/didChangeActiveDocument",
                new { path = Path.Combine(root, "src", "Program.cs") });
            await WaitForXraySectionAsync(h, id => id.EndsWith("cs.instructions.md", StringComparison.Ordinal));

            var after = await h.Client.InvokeAsync<List<RepoInstructionRow>>("settings/repoInstructions")
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.All(after, r => Assert.True(r.IsSent, r.File));
            Assert.Equal(Strings.RepoInstructionStateSent, after[1].State);

            var slash = await h.Client.InvokeWithParameterObjectAsync<SlashCommandResult>(
                "command/slash", new { text = "/instructions" }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.Contains("`.github/instructions/cs.instructions.md`", slash.Markdown);
            Assert.Contains("GitHub Copilot", slash.Markdown);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Instructions_WithoutAny_SayIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"host-instr-none-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        try
        {
            using var h = CreateHarness();
            await h.InitializeAsync(rootDir: root).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            var slash = await h.Client.InvokeWithParameterObjectAsync<SlashCommandResult>(
                "command/slash", new { text = "/instructions" }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.Equal(Strings.RepoInstructionsNone, slash.Markdown);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
