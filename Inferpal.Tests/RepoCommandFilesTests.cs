using System.IO;
using Inferpal.Config;
using Inferpal.Host;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The commands a repository already wrote for Copilot (<c>.github/prompts</c>), Claude Code (<c>.claude/commands</c>)
/// and Continue (<c>.continue/prompts</c>) are slash commands in Inferpal: named as their tool names them, their
/// variables filled with the words typed after them, their origin shown, their conflicts said.
/// </summary>
[Collection(PromptFilesCacheCollection.Name)]
public sealed class RepoCommandFilesTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"repo-cmd-{Guid.NewGuid():N}");

    public RepoCommandFilesTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        PromptFilesService.InvalidateCache();
    }

    public void Dispose()
    {
        PromptFilesService.InvalidateCache();
        try { Directory.Delete(_repo, recursive: true); } catch { }
    }

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_repo, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private IReadOnlyList<UserSlashTemplate> Load(InferpalConfig? config = null, string? workspace = null)
    {
        PromptFilesService.InvalidateCache();
        return SlashTemplates.Load(config ?? new InferpalConfig(), workspace ?? _repo);
    }

    private UserSlashTemplate Command(string name, InferpalConfig? config = null) =>
        Assert.Single(Load(config), t => t.Name == name);

    private string Run(string typed, InferpalConfig? config = null) =>
        Assert.IsType<SlashPromptAction>(SlashCommandRouter.Route(typed, Load(config))).Prompt;

    private string Prompts(InferpalConfig? config = null)
    {
        PromptFilesService.InvalidateCache();
        return RulesChecksPromptsCommandHandler.Prompts(_repo, ["/prompts"], config ?? new InferpalConfig()).Message!;
    }

    // ── Copilot ─────────────────────────────────────────────────────────────

    [Fact]
    public void ACopilotPromptFile_IsACommand_ItsInputFilledWithTheWordsTyped()
    {
        Write(".github/prompts/explain-test.prompt.md",
              "---\ndescription: Explain a failing test\nagent: agent\n---\nExplain why ${input:test:the test name} fails.");

        var cmd = Command("/explain-test");
        Assert.Equal("Explain a failing test", cmd.Hint);
        Assert.Equal("GitHub Copilot", cmd.Origin);
        Assert.Equal(".github/prompts/explain-test.prompt.md", cmd.Source);
        Assert.Equal("Explain why OrderTests.Total fails.", Run("/explain-test OrderTests.Total"));
    }

    [Fact]
    public void ACopilotName_IsTheCommand_WhenItIsOneWord()
    {
        Write(".github/prompts/a.prompt.md", "---\nname: fixit\n---\nFix it.");
        Write(".github/prompts/b-file.prompt.md", "---\nname: Fix Every Test\n---\nFix every test.");

        Assert.Equal("Fix it.", Command("/fixit").Text);
        Assert.Equal("Fix every test.", Command("/b-file").Text);      // a name with spaces cannot be typed
    }

    [Fact]
    public void ACopilotVariableInferpalDoesNotFill_IsNamed()
    {
        Write(".github/prompts/document.prompt.md", "Document ${selection} in ${file}.");

        Assert.Equal(["${selection}", "${file}"], Command("/document").Unfilled);
        Assert.Contains("${selection}", Prompts());
    }

    // ── Claude Code ─────────────────────────────────────────────────────────

    [Fact]
    public void AClaudeCommand_IsNamedByItsPath_ItsArgumentsFilled()
    {
        Write(".claude/commands/triage.md", "---\ndescription: Triage an issue\n---\nTriage issue $ARGUMENTS now.");
        Write(".claude/commands/frontend/component.md", "Make a component.");

        Assert.Equal("Claude Code", Command("/triage").Origin);
        Assert.Equal("Triage issue 1234 now.", Run("/triage 1234"));
        Assert.Equal(".claude/commands/frontend/component.md", Command("/frontend:component").Source);
    }

    [Fact]
    public void AClaudeShellLine_IsNotRun_TheModelIsAskedToRunIt_AndListingSaysSo()
    {
        Write(".claude/commands/pr.md", "- Current git status: !`git status`\nOpen a pull request.");

        var text = Command("/pr").Text;
        Assert.DoesNotContain("!`", text);
        Assert.Contains("`git status`", text);
        Assert.Contains(ModelPrompts.RepoCommandNotRun("git status"), text);
        Assert.Contains("git status", Prompts());
        Assert.Contains(Strings.PromptsCommandsNotRun("`git status`"), Prompts());
    }

    [Fact]
    public void AClaudePositionalArgument_IsNamed_NotFilled()
    {
        Write(".claude/commands/fix-issue.md", "Fix issue $0 in $ARGUMENTS[1].");

        Assert.Equal(["$0", "$ARGUMENTS[1]"], Command("/fix-issue").Unfilled);
    }

    // ── Continue ────────────────────────────────────────────────────────────

    [Fact]
    public void AContinuePromptWithoutOpeningDashes_KeepsItsHeaderOutOfTheText()
    {
        Write(".continue/prompts/core-unit-test.prompt",
              "name: Write Core Unit Test\ndescription: Generate unit tests\n---\nWrite jest tests for {{{ input }}}.");

        var cmd = Command("/core-unit-test");
        Assert.Equal("Generate unit tests", cmd.Hint);
        Assert.Equal("Continue", cmd.Origin);
        Assert.DoesNotContain("description:", cmd.Text);
        Assert.Equal("Write jest tests for src/util.ts.", Run("/core-unit-test src/util.ts"));
    }

    [Fact]
    public void AContinueMarkdownPrompt_IsACommand_OnlyWhenInvokable()
    {
        Write(".continue/prompts/agent.md", "---\nname: agent\ninvokable: true\n---\nRun cn -p \"{{prompt}}\"");
        Write(".continue/prompts/notes.md", "---\nname: notes\n---\nNot a prompt.");

        Assert.Equal(["{{prompt}}"], Command("/agent").Unfilled);
        Assert.DoesNotContain(Load(), t => t.Name == "/notes");
    }

    // ── The words typed, every format ───────────────────────────────────────

    [Fact]
    public void ACommandWithoutAPlaceholder_GetsTheWordsTypedAtTheEnd()
    {
        Write(".github/prompts/docs-check.prompt.md", "Check the XML docs.");
        Write(".inferpal/prompts/look-over.md", "Review this.");

        Assert.Equal("Check the XML docs.\n\nOrders.cs", Run("/docs-check Orders.cs"));
        Assert.Equal("Review this.\n\nthe service", Run("/look-over the service"));    // Inferpal's own format too
        Assert.Equal("Review this.", Run("/look-over"));                               // reference arm: nothing typed
    }

    // ── Conflicts and origin ────────────────────────────────────────────────

    [Fact]
    public void ACommandNamedLikeABuiltIn_IsDropped_AndListingSaysSo()
    {
        Write(".github/prompts/docs.prompt.md", "Document the C# types.");

        Assert.DoesNotContain(Load(), t => t.Name == "/docs");
        Assert.Contains(Strings.PromptsShadowedByBuiltIn("/docs"), Prompts());
    }

    [Fact]
    public void TwoFilesWithOneName_TheFirstInPrecedenceWins_TheOtherIsNamed()
    {
        Write(".inferpal/prompts/release.md", "INFERPAL-RELEASE");
        Write(".claude/commands/release.md", "CLAUDE-RELEASE");
        Write(".continue/prompts/release.prompt", "CONTINUE-RELEASE");

        Assert.Equal("INFERPAL-RELEASE", Command("/release").Text);
        var listing = Prompts();
        Assert.Contains(Strings.PromptsShadowedByFile("/release", ".inferpal/prompts/release.md"), listing);
        Assert.Equal(2, CountOf(listing, Strings.PromptsShadowedByFile("/release", ".inferpal/prompts/release.md")));
    }

    [Fact]
    public void TheOrigin_IsInTheAutocompleteHint_AndInHelp()
    {
        Write(".claude/commands/triage.md", "---\ndescription: Triage an issue\n---\nTriage $ARGUMENTS.");

        var templates = Load();
        var hint = Assert.Single(SlashCommandRouter.MatchCommands("/tri", templates)).Hint;
        Assert.Contains("Claude Code", hint);
        var help = Assert.IsType<SlashInfoAction>(SlashCommandRouter.Route("/help", templates)).Message;
        Assert.Contains("`/triage`", help);
        Assert.Contains("Claude Code", help);
        Assert.Contains(Strings.SlashCategoryCustom, help);
        // Reference arm: without custom commands /help is the catalog's, unchanged.
        Assert.Equal(SlashCommandRouter.BuildHelp(),
                     Assert.IsType<SlashInfoAction>(SlashCommandRouter.Route("/help", [])).Message);
    }

    // ── Where, and whose ────────────────────────────────────────────────────

    [Fact]
    public void CommandsAreReadAtTheRepositorysRoot_FromASolutionInSrc()
    {
        Write(".github/prompts/explain-test.prompt.md", "Explain.");
        Directory.CreateDirectory(Path.Combine(_repo, "src"));

        Assert.Contains(Load(workspace: Path.Combine(_repo, "src")), t => t.Name == "/explain-test");
    }

    [Fact]
    public void AToolTurnedOffInTheSettings_ReadsNoCommand()
    {
        Write(".claude/commands/triage.md", "Triage.");
        Write(".github/prompts/explain-test.prompt.md", "Explain.");
        var config = new InferpalConfig { RepoInstructionFamilies = "agents, copilot" };

        Assert.DoesNotContain(Load(config), t => t.Name == "/triage");
        Assert.Contains(Load(config), t => t.Name == "/explain-test");
    }

    [Fact]
    public void ARepositoryWithoutCommandFolders_KeepsTheListAsItWas()
    {
        Write(".inferpal/prompts/look-over.md", "Review.");
        var config = new InferpalConfig { PromptTemplates = "/hello=Hello {args}" };

        var names = Load(config).Select(t => (t.Name, t.Origin)).ToList();
        Assert.Equal([("/hello", (string?)null), ("/look-over", (string?)null)], names);
    }

    [Fact]
    public void ACommandLinkLeavingTheRepository_IsNotRead_AndListingSaysSo()
    {
        var outside = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"outside-{Guid.NewGuid():N}.md");
        File.WriteAllText(outside, "OUTSIDE-SECRET");
        var link = Path.Combine(_repo, ".claude", "commands", "leak.md");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try
        {
            try { File.CreateSymbolicLink(link, outside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }   // no privilege: undecided

            Assert.DoesNotContain(Load(), t => t.Name == "/leak");
            Assert.Contains(Strings.PromptsLinkLeaves(".claude/commands/leak.md"), Prompts());
        }
        finally { try { File.Delete(outside); } catch { } }
    }

    [Fact]
    public void ALinkCheckedOutAsText_IsFollowed_InsideTheRepositoryOnly()
    {
        // What git writes for a symbolic link without core.symlinks (Git for Windows' default): the target, as text.
        Write(".clinerules/workflows/release.md", "Cut a release: bump, tag, push.");
        Write(".claude/commands/release.md", "../../.clinerules/workflows/release.md");
        var outside = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"outside-{Guid.NewGuid():N}.md");
        File.WriteAllText(outside, "OUTSIDE-SECRET");
        try
        {
            Write(".claude/commands/leak.md", Path.GetRelativePath(Path.Combine(_repo, ".claude", "commands"), outside).Replace('\\', '/'));

            Assert.Equal("Cut a release: bump, tag, push.", Command("/release").Text);
            Assert.DoesNotContain(Load(), t => t.Name == "/leak");
            Assert.Contains(Strings.PromptsLinkLeaves(".claude/commands/leak.md"), Prompts());
        }
        finally { try { File.Delete(outside); } catch { } }
    }

    [Fact]
    public void TheListing_DoesNotOpenOnNoPromptFiles_AboveTheRepositorysCommands()
    {
        Write(".claude/commands/triage.md", "Triage.");

        var listing = Prompts();
        Assert.StartsWith(Strings.PromptsRepoHeader, listing);
        Assert.DoesNotContain(Strings.PromptsNone, listing);
        // Reference arm: a repository with no command at all still gets the sentence that says how to make one.
        Directory.Delete(Path.Combine(_repo, ".claude"), recursive: true);
        Assert.Equal(Strings.PromptsNone, Prompts());
    }

    [Fact]
    public void AOneLineCommand_ThatNamesNoFile_IsACommand()
    {
        Write(".claude/commands/short.md", "Summarize.md");        // reference arm: no such file, so it is the text

        Assert.Equal("Summarize.md", Command("/short").Text);
    }

    private static int CountOf(string text, string what) =>
        (text.Length - text.Replace(what, string.Empty).Length) / what.Length;
}

public partial class HostServerTests
{
    /// <summary>
    /// VS Code: the repository's commands are in the host's command list with their origin, run as prompts with the
    /// words typed, and are named by <c>/help</c> — the Core loader behind both editors.
    /// </summary>
    [Fact]
    public async Task RepositoryCommands_AreListedWithTheirOrigin_AndRunWithTheWordsTyped()
    {
        var root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"host-repo-cmd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        Directory.CreateDirectory(Path.Combine(root, ".claude", "commands"));
        File.WriteAllText(Path.Combine(root, ".claude", "commands", "triage.md"),
            "---\ndescription: Triage an issue\n---\nTriage issue $ARGUMENTS.");
        try
        {
            using var h = CreateHarness();
            await h.InitializeAsync(rootDir: root).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            var list = await h.Client.InvokeAsync<List<SlashCommandInfoDto>>("command/list")
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.Contains(list, c => c.Command == "/triage" && c.Hint.Contains("Claude Code", StringComparison.Ordinal));

            var run = await h.Client.InvokeWithParameterObjectAsync<SlashCommandResult>(
                "command/slash", new { text = "/triage 1234" }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.Equal("Triage issue 1234.", Assert.Single(run.Effects!, e => e.Kind == "sendAsPrompt").Value);

            var help = await h.Client.InvokeWithParameterObjectAsync<SlashCommandResult>(
                "command/slash", new { text = "/help" }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.Contains("`/triage`", help.Markdown);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
