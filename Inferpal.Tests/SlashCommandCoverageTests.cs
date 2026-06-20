using Inferpal.Localization;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// End-to-end-ish coverage of the slash-command surface: routes EVERY advertised command
// (and every alias) through the real router and asserts each lands on the correct action /
// tool — the exact layer where the "/search → web instead of codebase" mismatch lived.
// This is the automatable half of "test every command"; executing the resulting actions
// needs a live VS + Ollama and is exercised manually in the Exp hive.
public class SlashCommandCoverageTests
{
    private static readonly UserSlashTemplate[] NoTemplates = [];
    private static SlashAction Route(string prompt) => SlashCommandRouter.Route(prompt, NoTemplates);

    // ── 1. Every advertised command is wired (none falls through to "unknown command") ──

    [Fact]
    public void EveryBuiltInCommand_IsHandled()
    {
        foreach (var (cmd, _) in SlashCommandRouter.BuiltInCommands)
        {
            var action = Route(cmd); // no args → usage/info is fine, the UNKNOWN fallback is not
            if (action is SlashInfoAction info)
                Assert.True(info.Message != Strings.SlashHelp(cmd),
                    $"Command {cmd} fell through to the unknown-command help — not wired in Route().");
        }
    }

    [Fact]
    public void BuiltInCommands_HaveNoDuplicates()
    {
        var names = SlashCommandRouter.BuiltInCommands.Select(c => c.Cmd).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void EveryBuiltInCommand_AppearsInItsOwnAutocomplete()
    {
        foreach (var (cmd, _) in SlashCommandRouter.BuiltInCommands)
        {
            var matches = SlashCommandRouter.MatchCommands(cmd, NoTemplates);
            Assert.Contains(matches, m => m.Cmd == cmd);
        }
    }

    // ── 2. Tool-backed commands map to the expected registry tool (incl. aliases) ──

    [Theory]
    [InlineData("/restore a.cs",        "restore_file")]
    [InlineData("/read a.cs",           "read_file")]
    [InlineData("/ls .",                "list_files")]
    [InlineData("/grep . foo",          "search_in_files")]
    [InlineData("/run echo hi",         "run_command")]
    [InlineData("/fetch http://x",      "fetch_url")]
    [InlineData("/search-web q",        "web_search")]
    [InlineData("/search q",            "web_search")]      // legacy alias
    [InlineData("/web_search q",        "web_search")]      // legacy alias
    [InlineData("/search-code q",       "search_codebase")]
    [InlineData("/codebase q",          "search_codebase")] // alias
    [InlineData("/git",                 "get_git_status")]
    [InlineData("/diff",                "get_git_status")]
    [InlineData("/map",                 "generate_project_map")]
    [InlineData("/map a.cs",            "analyze_code")]
    [InlineData("/solution",            "get_solution_info")]
    [InlineData("/build",               "get_diagnostics")]
    public void ToolCommand_RoutesToExpectedTool(string prompt, string expectedTool)
    {
        var action = Assert.IsType<SlashToolAction>(Route(prompt));
        Assert.Equal(expectedTool, action.Tool);
    }

    // ── 3. Code-action commands map to the expected kind ──

    [Theory]
    [InlineData("/explain",  "Explain")]
    [InlineData("/fix",      "Fix")]
    [InlineData("/review",   "Review")]
    [InlineData("/refactor", "Refactor")]
    [InlineData("/test",     "Test")]
    [InlineData("/doc",      "Doc")]
    public void CodeActionCommand_RoutesToExpectedKind(string prompt, string kind)
    {
        var action = Assert.IsType<SlashCodeAction>(Route(prompt));
        Assert.Equal(kind, action.Kind.ToString());
    }

    // ── 4. Stateful commands delegate to the VM with the expected id ──

    [Theory]
    [InlineData("/clear",             "Clear")]
    [InlineData("/test-build-banner", "TestBuildBanner")]
    [InlineData("/model",             "Model")]
    [InlineData("/tools",             "Tools")]
    [InlineData("/export",            "Export")]
    [InlineData("/context",           "Context")]
    [InlineData("/memory",            "Memory")]
    [InlineData("/index",             "Index")]
    [InlineData("/commit",            "Commit")]
    [InlineData("/commit-exec",       "CommitExec")]
    [InlineData("/fix-build",         "FixBuild")]
    [InlineData("/history",           "History")]
    [InlineData("/phistory",          "PHistory")]
    [InlineData("/models",            "Models")]
    [InlineData("/agent-step",        "AgentStep")]
    [InlineData("/resume",            "Resume")]
    [InlineData("/plan",              "Plan")]
    [InlineData("/prompts",           "Prompts")]
    [InlineData("/hardware",          "Hardware")]
    [InlineData("/setup",             "Setup")]
    [InlineData("/note",              "Note")]
    [InlineData("/notes",             "Notes")]
    [InlineData("/snippets",          "Snippets")]
    [InlineData("/template",          "Template")]
    [InlineData("/docs",              "Docs")]
    [InlineData("/check",             "Check")]
    [InlineData("/rules",             "Rules")]
    [InlineData("/checks",            "Checks")]
    [InlineData("/diagnostics",       "Diagnostics")]
    [InlineData("/undo-run",          "UndoRun")]
    public void StatefulCommand_DelegatesWithExpectedId(string prompt, string id)
    {
        var action = Assert.IsType<SlashDelegatedAction>(Route(prompt));
        Assert.Equal(id, action.Id.ToString());
    }

    // ── 5. Meta + unknown ──

    [Fact]
    public void Help_ReturnsFullHelp()
    {
        var action = Assert.IsType<SlashInfoAction>(Route("/help"));
        Assert.Equal(Strings.SlashHelpAll, action.Message);
    }

    [Fact]
    public void UnknownCommand_FallsBackToHelp()
    {
        var action = Assert.IsType<SlashInfoAction>(Route("/definitely-not-a-command"));
        Assert.Equal(Strings.SlashHelp("/definitely-not-a-command"), action.Message);
    }
}
