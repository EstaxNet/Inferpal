using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Covers the pure slash-command routing extracted from the tool-window VM: tokenisation,
// usage validation, tool-argument building, user-template parsing/expansion, and the
// autocomplete matching. Execution of the returned actions stays in the VM (not tested here).
public class SlashCommandRouterTests
{
    private static readonly UserSlashTemplate[] NoTemplates = [];

    private static SlashAction Route(string prompt) => SlashCommandRouter.Route(prompt, NoTemplates);

    /// <summary>Serialises anonymous-type args so tests can assert their exact JSON shape.</summary>
    private static string Json(object o) => JsonSerializer.Serialize(o);

    // ── Tool invocations: argument building ───────────────────────────────────

    [Fact]
    public void Read_BuildsPathFromAllParts_AndAttachesAsFileName()
    {
        var action = Assert.IsType<SlashToolAction>(Route(@"/read C:\src\My File.cs"));
        Assert.Equal("read_file", action.Tool);
        Assert.Equal("""{"path":"C:\\src\\My File.cs"}""", Json(action.Args));
        Assert.Equal("My File.cs", action.AttachAs);
    }

    [Fact]
    public void Ls_OptionalPattern()
    {
        var bare = Assert.IsType<SlashToolAction>(Route("/ls src"));
        Assert.Equal("list_files", bare.Tool);
        Assert.Equal("""{"path":"src"}""", Json(bare.Args));

        var withPattern = Assert.IsType<SlashToolAction>(Route("/ls src *.cs"));
        Assert.Equal("""{"path":"src","pattern":"*.cs"}""", Json(withPattern.Args));
    }

    [Fact]
    public void Grep_OptionalFilePattern()
    {
        var two = Assert.IsType<SlashToolAction>(Route("/grep src TODO"));
        Assert.Equal("search_in_files", two.Tool);
        Assert.Equal("""{"path":"src","pattern":"TODO"}""", Json(two.Args));

        var three = Assert.IsType<SlashToolAction>(Route("/grep src TODO *.cs"));
        Assert.Equal("""{"path":"src","pattern":"TODO","file_pattern":"*.cs"}""", Json(three.Args));
    }

    [Fact]
    public void Search_JoinsQuery_AndWebSearchIsAlias()
    {
        var search = Assert.IsType<SlashToolAction>(Route("/search-web ollama vulkan backend"));
        Assert.Equal("web_search", search.Tool);
        Assert.Equal("""{"query":"ollama vulkan backend"}""", Json(search.Args));

        // Legacy aliases still route to the same tool.
        foreach (var legacy in new[] { "/search hello world", "/web_search hello world" })
        {
            var alias = Assert.IsType<SlashToolAction>(Route(legacy));
            Assert.Equal("web_search", alias.Tool);
            Assert.Equal("""{"query":"hello world"}""", Json(alias.Args));
        }
    }

    [Fact]
    public void SearchCode_RoutesToCodebase_NotWeb()
    {
        var code = Assert.IsType<SlashToolAction>(Route("/search-code retry with backoff"));
        Assert.Equal("search_codebase", code.Tool);
        Assert.Equal("""{"query":"retry with backoff"}""", Json(code.Args));

        var alias = Assert.IsType<SlashToolAction>(Route("/codebase auth token validation"));
        Assert.Equal("search_codebase", alias.Tool);
        Assert.Equal("""{"query":"auth token validation"}""", Json(alias.Args));
    }

    [Fact]
    public void Run_JoinsFullCommandLine()
    {
        var action = Assert.IsType<SlashToolAction>(Route("/run git log --oneline -5"));
        Assert.Equal("run_command", action.Tool);
        Assert.Equal("""{"command":"git log --oneline -5"}""", Json(action.Args));
    }

    [Fact]
    public void Diff_IncludesDiffFlag_AndAttachesAsChip()
    {
        var bare = Assert.IsType<SlashToolAction>(Route("/diff"));
        Assert.Equal("get_git_status", bare.Tool);
        Assert.Equal("""{"include_diff":true}""", Json(bare.Args));
        Assert.Equal("📊 git diff", bare.AttachAs);

        var withPath = Assert.IsType<SlashToolAction>(Route("/diff src/My Project"));
        Assert.Equal("""{"path":"src/My Project","include_diff":true}""", Json(withPath.Args));
    }

    [Fact]
    public void Map_RoutesToProjectMapOrTraceDependency()
    {
        var bare = Assert.IsType<SlashToolAction>(Route("/map"));
        Assert.Equal("generate_project_map", bare.Tool);

        var withPath = Assert.IsType<SlashToolAction>(Route("/map src/Program.cs"));
        Assert.Equal("analyze_code", withPath.Tool);
        Assert.Equal("""{"mode":"callgraph","path":"src/Program.cs"}""", Json(withPath.Args));
    }

    [Theory]
    [InlineData("/git",      "get_git_status")]
    [InlineData("/solution", "get_solution_info")]
    [InlineData("/build",    "get_diagnostics")]
    public void StatusCommands_OptionalPathArgument(string cmd, string tool)
    {
        var bare = Assert.IsType<SlashToolAction>(Route(cmd));
        Assert.Equal(tool, bare.Tool);
        Assert.Equal("{}", Json(bare.Args));

        var withPath = Assert.IsType<SlashToolAction>(Route(cmd + " sub/dir"));
        Assert.Equal("""{"path":"sub/dir"}""", Json(withPath.Args));
    }

    // ── Usage validation ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("/read",  "/read <path>")]
    [InlineData("/ls",    "/ls <path> [pattern]")]
    [InlineData("/grep",  "/grep <dir> <pattern> [file_pattern]")]
    [InlineData("/grep x","/grep <dir> <pattern> [file_pattern]")]
    [InlineData("/run",   "/run <PowerShell command>")]
    [InlineData("/fetch", "/fetch <url>")]
    [InlineData("/search-web","/search-web <query>")]
    public void MissingArguments_ReturnUsageInfo(string prompt, string usage)
    {
        var info = Assert.IsType<SlashInfoAction>(Route(prompt));
        Assert.Equal(Strings.SlashUsage(usage), info.Message);
    }

    // ── Code actions, delegated commands, meta ────────────────────────────────

    [Fact]
    public void CodeActions_MapToKind()
    {
        // Internal enums cannot appear in public [Theory] signatures — iterate inline instead.
        (string Prompt, SlashCodeActionKind Kind)[] cases =
        [
            ("/explain",  SlashCodeActionKind.Explain),
            ("/FIX",      SlashCodeActionKind.Fix),       // command matching is case-insensitive
            ("/review",   SlashCodeActionKind.Review),
            ("/refactor", SlashCodeActionKind.Refactor),
            ("/test",     SlashCodeActionKind.Test),
            ("/doc",      SlashCodeActionKind.Doc),
        ];
        foreach (var (prompt, kind) in cases)
        {
            var action = Assert.IsType<SlashCodeAction>(Route(prompt));
            Assert.Equal(kind, action.Kind);
        }
    }

    [Fact]
    public void StatefulCommands_AreDelegatedWithParts()
    {
        (string Prompt, SlashCommandId Id)[] cases =
        [
            ("/clear",          SlashCommandId.Clear),
            ("/model devstral", SlashCommandId.Model),
            ("/tools on",       SlashCommandId.Tools),
            ("/commit-exec m",  SlashCommandId.CommitExec),
            ("/agent-step",     SlashCommandId.AgentStep),
            ("/checks init",    SlashCommandId.Checks),
        ];
        foreach (var (prompt, id) in cases)
        {
            var action = Assert.IsType<SlashDelegatedAction>(Route(prompt));
            Assert.Equal(id, action.Id);
            Assert.Equal(prompt.Split(' '), action.Parts);
        }
    }

    [Fact]
    public void Help_ShowsFullHelp_AndUnknownShowsCommandHelp()
    {
        var help = Assert.IsType<SlashInfoAction>(Route("/help"));
        Assert.Equal(Strings.SlashHelpAll, help.Message);

        var unknown = Assert.IsType<SlashInfoAction>(Route("/frobnicate now"));
        Assert.Equal(Strings.SlashHelp("/frobnicate"), unknown.Message);
    }

    // ── User templates ─────────────────────────────────────────────────────────

    [Fact]
    public void ParseUserTemplates_SkipsDisabledAndMalformedLines()
    {
        var parsed = SlashCommandRouter.ParseUserTemplates(
            "/standup=Summarize {args} briefly\n" +
            "#/off=disabled entry\n" +          // '#' prefix = disabled
            "noslash=not a command\n" +          // name must start with '/'
            "/empty=\n" +                        // empty text
            "broken line without equals\n" +
            "  /UPPER = Trimmed Text  \n");      // trimmed, name lower-cased

        Assert.Equal(2, parsed.Count);
        Assert.Equal(new UserSlashTemplate("/standup", "Summarize {args} briefly"), parsed[0]);
        Assert.Equal(new UserSlashTemplate("/upper", "Trimmed Text"), parsed[1]);
    }

    [Fact]
    public void UserTemplate_ExpandsArgsPlaceholder()
    {
        var templates = SlashCommandRouter.ParseUserTemplates("/standup=Summarize {args} briefly");

        var withArgs = Assert.IsType<SlashPromptAction>(SlashCommandRouter.Route("/standup my day", templates));
        Assert.Equal("Summarize my day briefly", withArgs.Prompt);

        var noArgs = Assert.IsType<SlashPromptAction>(SlashCommandRouter.Route("/standup", templates));
        Assert.Equal("Summarize  briefly", noArgs.Prompt);
    }

    // ── Autocomplete matching ──────────────────────────────────────────────────

    [Fact]
    public void MatchCommands_PrefixFiltersBuiltInsAndUserTemplates()
    {
        var templates = SlashCommandRouter.ParseUserTemplates("/release=Draft the release notes for {args}");

        var matches = SlashCommandRouter.MatchCommands("/re", templates);
        var cmds    = matches.Select(m => m.Cmd).ToList();

        Assert.Contains("/read", cmds);
        Assert.Contains("/refactor", cmds);
        Assert.Contains("/restore", cmds);
        Assert.Contains("/release", cmds);     // user template included
        Assert.DoesNotContain("/run", cmds);
    }

    [Fact]
    public void MatchCommands_EmptyUnlessSpacelessSlashPrefix()
    {
        Assert.Empty(SlashCommandRouter.MatchCommands("", NoTemplates));
        Assert.Empty(SlashCommandRouter.MatchCommands("hello", NoTemplates));
        Assert.Empty(SlashCommandRouter.MatchCommands("/read C:", NoTemplates)); // space → typing args
    }

    [Fact]
    public void MatchCommands_TruncatesLongUserTemplateHints()
    {
        var longText  = new string('a', 80);
        var templates = new List<UserSlashTemplate> { new("/long", longText) };

        var match = Assert.Single(SlashCommandRouter.MatchCommands("/long", templates));
        Assert.Equal(51, match.Hint.Length);          // 50 chars + ellipsis
        Assert.EndsWith("…", match.Hint);
    }
}
