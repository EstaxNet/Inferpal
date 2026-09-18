using System.IO;
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
                Assert.True(info.Message != SlashCommandRouter.UnknownCommandMessage(cmd),
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
    [InlineData("/replay",            "Replay")]
    [InlineData("/xray",              "Xray")]
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
        Assert.Equal(SlashCommandRouter.BuildHelp(), action.Message);
    }

    // ── /help is generated, and both directions of the catalogue are locked ──────────

    [Fact]
    public void Help_ListsEveryCommandOfTheCatalogue_UnderACategoryTitle()
    {
        var help = SlashCommandRouter.BuildHelp();

        foreach (var (cmd, hint, category) in SlashCommandRouter.Catalog)
        {
            Assert.Contains($"`{cmd}` — {hint}", help);
            Assert.Contains($"**{SlashCommandRouter.CategoryTitle(category)}**", help);
        }
    }

    [Fact]
    public void EveryRoutedCommand_IsAdvertisedInTheCatalogue()
    {
        // The reverse of EveryBuiltInCommand_IsHandled, and the check that was missing: `/docs`
        // was routed and worked, but had never been listed, so it existed in neither
        // autocomplete nor `/help`. Reading the `case "/x":` labels out of the router's own
        // source is the only way to enumerate what Route() actually accepts.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "Inferpal.Core", "Services", "SlashCommandRouter.cs"));
        var routed = System.Text.RegularExpressions.Regex
            .Matches(source, @"case ""(/[a-z0-9_-]+)"":")
            .Select(m => m.Groups[1].Value)
            .Distinct();

        // Aliases and internals that deliberately stay out of the advertised list. Anything not
        // named here must be advertised — that is the point of the check.
        string[] unadvertised =
        [
            "/benchmark",          // alias of /bench
            "/search",             // legacy alias of /search-web
            "/web_search",         // legacy alias of /search-web
            "/codebase",           // legacy alias of /search-code
            "/commit-exec",        // second leg of /commit, never typed directly
            "/test-build-banner",  // developer-only probe
        ];

        var advertised = SlashCommandRouter.Catalog.Select(c => c.Cmd).ToHashSet();
        foreach (var cmd in routed.Except(unadvertised))
            Assert.True(advertised.Contains(cmd),
                $"{cmd} is routed but missing from SlashCommandRouter.Catalog — it would be " +
                "invisible in both autocompletes and in /help.");
    }

    /// <summary>
    /// Every command <b>quoted</b> in a message shown to the user can actually be typed — and the
    /// ten languages quote the same ones.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The repository has paid for both halves. A quoted command that does not exist: <c>/search</c>,
    /// frozen by hand in the "unknown command" message of all ten languages. A quoted command that
    /// exists but does not do what the sentence promises: <c>/history</c>, offered as a way to
    /// recover a file when it searches saved conversations — <c>RecoveryMessageTests</c> holds that
    /// half, by routing the message for real.
    /// </para>
    /// <para>
    /// Here, the general property: a word quoted in backticks and starting with <c>/</c> must route.
    /// ⚠ The second check aims at the most exposed channel — the <c>.resx</c> are translated <b>by
    /// hand</b>, so a translator can perfectly well write the command in their own language: the
    /// instruction stays correct in English and becomes impossible to follow elsewhere, without a
    /// single build complaining. The quoted sets must be identical.
    /// </para>
    /// <para>
    /// Measured at zero violations (960 occurrences, 25 distinct commands, 96 per
    /// language) — free to lock, so now rather than later.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryQuotedCommand_CanBeTyped_AndTheTenLanguagesQuoteTheSameOnes()
    {
        // Written by `/prompts init` as a prompt FILE, so it routes as a user template rather than
        // through the router: the only exemption, and it is nominative.
        string[] scaffoldedTemplates = ["/review-security"];

        var localization = Path.Combine(RepoRoot(), "Inferpal.Core", "Localization");
        var quoted = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var occurrences = 0;

        foreach (var resx in Directory.EnumerateFiles(localization, "Strings*.resx"))
        {
            var words = new HashSet<string>(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex
                         .Matches(File.ReadAllText(resx), @"`(/[A-Za-z][^`]*)`"))
            {
                // The argument placeholder (`<id>`, `<path>`) is not part of the command name.
                var word = System.Text.RegularExpressions.Regex
                    .Replace(m.Groups[1].Value, "<[^>]*>", "x").Trim().Split(' ')[0];
                words.Add(word);
                occurrences++;
            }
            quoted[Path.GetFileName(resx)] = words;
        }

        // Witnesses: ten files read and hundreds of citations found. A broken glob or a regex that
        // no longer matches would green both checks below while reading nothing.
        Assert.True(quoted.Count == 10, $"{quoted.Count} resource file(s) read instead of 10.");
        Assert.True(occurrences >= 200,
            $"Only {occurrences} quoted command(s) found — the reading is dead.");

        foreach (var (file, words) in quoted)
            foreach (var word in words.Except(scaffoldedTemplates))
                Assert.True(SlashCommandRouter.IsBuiltIn(word),
                    $"{file} quotes `{word}`, which the router does not know: the user is reading an "
                    + "instruction they cannot follow.");

        var neutral = quoted["Strings.resx"];
        foreach (var (file, words) in quoted.Where(p => p.Key != "Strings.resx"))
        {
            Assert.True(words.SetEquals(neutral),
                $"{file} does not quote the same commands as Strings.resx — extra: "
                + $"[{string.Join(", ", words.Except(neutral))}], missing: "
                + $"[{string.Join(", ", neutral.Except(words))}]. A command is translated the way an "
                + "identifier is: not at all.");
        }
    }

    /// <summary>Repo root = first ancestor of the test bin folder containing README.md.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void UnknownCommand_FallsBackToHelp()
    {
        var action = Assert.IsType<SlashInfoAction>(Route("/definitely-not-a-command"));
        Assert.Equal(SlashCommandRouter.UnknownCommandMessage("/definitely-not-a-command"), action.Message);

        // The point of the repair: the list is GENERATED, so it can no longer drift. It was
        // frozen by hand in all ten languages and had reached 26 of the 57 shipped commands,
        // plus one (`/search`) that does not exist -- shown at the moment the user has just
        // typed something the product did not recognise.
        foreach (var (cmd, _, _) in SlashCommandRouter.Catalog)
            Assert.Contains($"`{cmd}`", action.Message, StringComparison.Ordinal);
    }
}
