using System.IO;
using System.Reflection;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Inferpal.Services.Docs;
using Inferpal.Services.Execution;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Presentation;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ A tool the user turns off (<c>/tools off</c>, the Tools page: <c>disabledTools</c>) is never offered to the model nor
/// run for it, is counted nowhere, and is said — and only the MODEL loses it: a gesture of the user that runs the tool
/// itself (<c>@problems</c>, <c>/diff</c>, <c>/tdd</c>'s test run) is not the model's call. An MCP server's entry turns its
/// tools off the same way (<c>disabledTools</c>, Roo's field).
/// </summary>
[Collection(SignalCollection.Name)]
public sealed class ToolSwitchesTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    private readonly List<IAsyncDisposable> _asyncDisposables = [];

    public ToolSwitchesTests() => TestRagStore.Redirect();

    public void Dispose()
    {
        foreach (var d in _asyncDisposables) d.DisposeAsync().AsTask().GetAwaiter().GetResult();
        foreach (var d in _disposables) d.Dispose();
    }

    /// <summary>A server that lists the given tools and records the calls it receives.</summary>
    private sealed class ListingClient(string name, IReadOnlyList<McpToolInfo> tools) : IMcpClient
    {
        public List<string> Calls { get; } = [];
        public string ServerName => name;
        public string? LastError => null;
        public bool NeedsAuthorization => false;
        public event Action? ToolsChanged { add { } remove { } }
        public event Action? Closed { add { } remove { } }
        public Task<bool> StartAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<McpToolInfo>?> ListToolsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpToolInfo>?>(tools);
        public Task<string> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct)
        {
            Calls.Add(toolName);
            return Task.FromResult("done");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { path = new { type = "string", description = "The file, relative to the project." } },
    });

    private static readonly JsonElement NoArgs = JsonSerializer.SerializeToElement(new { });

    private static List<McpToolInfo> Heavy(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new McpToolInfo(
            $"tool_{i:000}",
            $"Operation number {i} of the integrated development environment. " + string.Concat(Enumerable.Repeat(
                "It works on the project the IDE has open, reads its state and reports what it finds in detail. ", 15)),
            Schema))];

    private static readonly List<McpToolInfo> Small =
    [
        new("list_issues", "Lists the open issues of the repository.", Schema),
        new("create_issue", "Opens a new issue with a title and a body.", Schema),
        new("ping", "Checks that the issue tracker answers.", Schema),
    ];

    private sealed record Built(ToolRegistry Tools, InferpalConfig Config, Dictionary<string, ListingClient> Clients);

    /// <param name="entries">Each server's entry beyond its command (<c>"disabledTools": [...]</c>), by server.</param>
    private async Task<Built> Build(int? window, (string Server, IReadOnlyList<McpToolInfo> Tools)[] servers,
                                    Dictionary<string, string>? entries = null)
    {
        var clients = servers.ToDictionary(s => s.Server, s => new ListingClient(s.Server, s.Tools));
        var json = "{" + string.Join(",", servers.Select(s =>
            $"\"{s.Server}\": {{ \"command\": \"x\"{(entries?.TryGetValue(s.Server, out var extra) == true ? ", " + extra : "")} }}")) + "}";
        var config = new InferpalConfig { RagEnabled = false, McpEnabled = servers.Length > 0, McpServersJson = servers.Length > 0 ? json : "" };
        var mcp = new McpToolService(config, new NoopApproval(), cfg => clients[cfg.Name], [TimeSpan.FromMilliseconds(5)]);
        _asyncDisposables.Add(mcp);
        await mcp.RefreshAsync();
        var client = new FakeInferenceProvider();
        var index = new ProjectIndexService(client, config, new LspSemanticProvider());
        _disposables.Add(index);
        var editor = new NullEditorSurface();
        var tools = new ToolRegistry(editor, new NoopApproval(), config, index, client, new ProjectMapService(editor, index), mcp,
                                     new DocsIndexService(client, config), new OpenDocumentOverlay(), new NullDebugSession());
        _disposables.Add(tools);
        if (window is { } w) tools.WindowInUse = () => w;
        return new(tools, config, clients);
    }

    private static List<string> Names(IToolRegistry tools) => [.. tools.Definitions.Select(d => d.Function.Name)];

    private static Task<string> Search(ToolRegistry tools, string query) =>
        tools.ExecuteAsync("tool_search", JsonSerializer.SerializeToElement(new { query }), CancellationToken.None);

    private static ToolsCommandResult Tools(Built b, params string[] parts) =>
        ToolsCommandHandler.Handle(["/tools", .. parts], b.Tools, b.Config, sessionTools: true);

    [Fact]
    public async Task ABuiltInTurnedOff_LeavesTheDefinitions_TheOthersKeepTheirOrder()
    {
        var b = await Build(32_768, []);
        var before = Names(b.Tools);
        Assert.Contains("run_command", before);   // reference arm: offered while nothing turns it off

        b.Config.DisabledTools = "run_command";
        Assert.Equal([.. before.Where(n => n != "run_command")], Names(b.Tools));
        // Still read as a call when written as text — and refused with its cause; left unread, it was shown as the answer.
        Assert.Contains("run_command", b.Tools.CallableNames);
        b.Config.DisabledTools = "read_file";
        foreach (var readOnly in new IToolRegistry[] { new PlanModeToolRegistry(b.Tools), new Services.Tasks.BackgroundTaskToolRegistry(b.Tools) })
        {
            Assert.Contains("read_file", readOnly.CallableNames);
            Assert.DoesNotContain("write_file", readOnly.CallableNames);   // the decorator's own rule holds
        }
    }

    [Fact]
    public async Task ACallWrittenAsTextToAToolTurnedOff_IsRefused_NotShownAsTheAnswer()
    {
        var b = await Build(32_768, []);
        b.Config.DisabledTools = "run_command";
        var (calls, _) = Services.Agent.InlineToolCallParser.FromContent(
            """{"name": "run_command", "arguments": {"command": "dotnet build"}}""", b.Tools.CallableNames.ToHashSet());
        var call = Assert.Single(calls!);
        Assert.Equal("run_command", call.Function.Name);
        Assert.Contains("turned off by the user", await Services.Agent.AgentOrchestrator.ExecuteToolSafeAsync(
            b.Tools, call.Function, replyCut: false, CancellationToken.None));
    }

    [Fact]
    public async Task AModelCallToAToolTurnedOff_IsRefused_AndTheToolNeverRuns()
    {
        var b = await Build(32_768, [("tracker", Small)]);
        b.Config.DisabledTools = "tracker/list_issues";

        var refused = await Services.Agent.AgentOrchestrator.ExecuteToolSafeAsync(b.Tools, "mcp__tracker__list_issues", NoArgs, CancellationToken.None);
        Assert.StartsWith("Error: ", refused);
        Assert.Contains("turned off by the user", refused);
        Assert.Contains("/tools on tracker/list_issues", refused);
        Assert.Empty(b.Clients["tracker"].Calls);

        // Reference arm: the tool beside it runs.
        Assert.Equal("done", await Services.Agent.AgentOrchestrator.ExecuteToolSafeAsync(
            b.Tools, "mcp__tracker__create_issue", NoArgs, CancellationToken.None));
        Assert.Equal(["create_issue"], b.Clients["tracker"].Calls);
    }

    [Fact]
    public async Task ADecorator_ForwardsTheRefusal_PlanModeAndStepModeIncluded()
    {
        var b = await Build(32_768, []);
        b.Config.DisabledTools = "read_file";
        var args = JsonSerializer.SerializeToElement(new { path = "nothing-here.txt" });

        foreach (var wrapped in new IToolRegistry[]
                 {
                     new PlanModeToolRegistry(b.Tools),   // read_file is one of plan mode's own tools
                     new StepModeToolRegistry(b.Tools, _ => Task.CompletedTask),
                     new Services.Tasks.BackgroundTaskToolRegistry(b.Tools),
                 })
        {
            var said = await Services.Agent.AgentOrchestrator.ExecuteToolSafeAsync(wrapped, "read_file", args, CancellationToken.None);
            Assert.Contains("turned off by the user", said);
        }
    }

    [Fact]
    public void EveryDecoratorOfTheRegistry_ForwardsWhatIsTurnedOff()
    {
        // The property, not a list: a registry built around another one answers what the inner one turns off — answered
        // by the interface's default, a tool turned off runs again behind the decorator. Every assembly of the product:
        // the agent protocol's sessions wrap the registry in the host's own (ToolEventRegistry).
        var assemblies = new List<Assembly> { typeof(IToolRegistry).Assembly, typeof(Inferpal.Host.HostServer).Assembly };
#if WINDOWS
        assemblies.Add(typeof(Inferpal.ToolWindow.InferpalSettingsData).Assembly);   // the VSIX exists on this leg only
#endif
        var decorators = assemblies.SelectMany(a => a.GetTypes())
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IToolRegistry).IsAssignableFrom(t)
                        && t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                            .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IToolRegistry))))
            .ToList();
        Assert.True(decorators.Count >= 3, $"only {decorators.Count} decorators found: the scan reads nothing");
        Assert.Empty(decorators.Where(t => t.GetMethod(nameof(IToolRegistry.TurnedOffRefusal),
                                                       BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) is null)
                               .Select(t => t.FullName));
    }

    [Fact]
    public async Task AGestureOfTheUser_StillRunsATurnedOffTool()
    {
        // @problems, /diff, /tdd's test run call the registry themselves: turning a tool off lightens the model's requests,
        // it does not take a command away from the user.
        var b = await Build(32_768, [("tracker", Small)]);
        b.Config.DisabledTools = "tracker/list_issues";
        Assert.Equal("done", await b.Tools.ExecuteAsync("mcp__tracker__list_issues", NoArgs, CancellationToken.None));
        Assert.Equal(["list_issues"], b.Clients["tracker"].Calls);
    }

    [Fact]
    public async Task AnMcpToolTurnedOff_WeighsNothingInTheSplit_AndToolSearchSaysSo()
    {
        var b = await Build(32_768, [("ide", Heavy(93)), ("tracker", Small)]);
        var description = b.Tools.Definitions.Single(d => d.Function.Name == "tool_search").Function.Description;
        Assert.Contains("mcp__ide__tool_001", description);   // reference arm

        b.Config.DisabledTools = "ide/tool_001";
        description = b.Tools.Definitions.Single(d => d.Function.Name == "tool_search").Function.Description;
        Assert.DoesNotContain("mcp__ide__tool_001", description);
        Assert.Contains("server 'ide' (92 tools)", description);
        var said = await Search(b.Tools, "select:tool_001");
        Assert.Contains("turned off by the user", said);
        Assert.DoesNotContain("No tool on demand is named", said);

        // Turned off down to what fits, the server is declared again: nothing waits, tool_search is not offered.
        b.Config.DisabledTools = string.Join("\n", Enumerable.Range(1, 90).Select(i => $"ide/tool_{i:000}"));
        var names = Names(b.Tools);
        Assert.DoesNotContain("tool_search", names);
        Assert.Contains("mcp__ide__tool_093", names);
        Assert.DoesNotContain("mcp__ide__tool_001", names);
    }

    [Fact]
    public async Task ATool_IsTurnedOffByTheNameTheListShows_NeverByANameRebuiltFromIt()
    {
        // `my-server` and `my.server` clean up to the same tool names, so the second one's are renamed `_2`; a server named
        // `org/github` holds a slash. Rebuilding a name from `server/tool` reached the wrong tool, or none.
        var b = await Build(32_768, [("my-server", Small), ("my.server", Small), ("org/github", Small)]);
        Assert.Contains("mcp__my_server__ping_2", Names(b.Tools));   // witness: the rename happened

        Assert.True(Tools(b, "off", "my.server/ping", "org/github/ping").SaveConfig);
        var names = Names(b.Tools);
        Assert.DoesNotContain("mcp__my_server__ping_2", names);
        Assert.Contains("mcp__my_server__ping", names);              // the other server's tool stays
        Assert.DoesNotContain("mcp__org_github__ping", names);
        Assert.Equal(ToolState.OffByUser, b.Tools.Profile().Single(e => e.Key == "org/github/ping").State);
    }

    [Fact]
    public void AServersEntry_ReadsItsDisabledTools_AndEveryRewriteKeepsThem()
    {
        var parsed = McpServerConfig.Parse("""{ "s": { "command": "x", "disabledTools": ["a", " b ", "a", ""] } }""").Single();
        Assert.Equal(["a", "b"], parsed.DisabledTools);
        Assert.Equal(["a", "b"], McpServerConfig.Parse(McpServerConfig.Serialize([parsed])).Single().DisabledTools);
        Assert.Null(McpServerConfig.Parse("""{ "s": { "command": "x" } }""").Single().DisabledTools);   // reference arm

        // The Visual Studio list rebuilds an entry from the fields it shows: what it does not show is carried over.
        var stdio = McpServerConfig.FromEditor(parsed, "s", "y", [], new Dictionary<string, string>(), null, null, enabled: true);
        Assert.Equal(["a", "b"], stdio.DisabledTools);
        var http = McpServerConfig.Parse("""{ "h": { "url": "https://x/mcp", "type": "sse", "disabledTools": ["c"] } }""").Single();
        var rebuilt = McpServerConfig.FromEditor(http, "h", null, [], new Dictionary<string, string>(), "https://x/mcp", null, enabled: true);
        Assert.Equal(["c"], rebuilt.DisabledTools);
        Assert.True(rebuilt.DeclaresSse);
    }

    [Fact]
    public async Task AToolItsServersEntryTurnsOff_IsNotOffered_AndIsTurnedBackOnThere()
    {
        var b = await Build(32_768, [("tracker", Small)], new() { ["tracker"] = "\"disabledTools\": [\"list_issues\"]" });
        Assert.DoesNotContain("mcp__tracker__list_issues", Names(b.Tools));
        Assert.Contains("mcp__tracker__create_issue", Names(b.Tools));
        Assert.Equal(ToolState.OffByEntry, b.Tools.Profile().Single(e => e.Key == "tracker/list_issues").State);

        var refused = await Services.Agent.AgentOrchestrator.ExecuteToolSafeAsync(b.Tools, "mcp__tracker__list_issues", NoArgs, CancellationToken.None);
        Assert.Contains("turned off by the entry of the MCP server 'tracker'", refused);

        var on = Tools(b, "on", "tracker/list_issues");
        Assert.False(on.SaveConfig);
        Assert.Equal(Strings.SlashToolsOffByEntry("tracker/list_issues", "tracker"), on.Message);
        Assert.Equal(string.Empty, b.Config.DisabledTools);
    }

    [Fact]
    public async Task TheToolsCommand_TurnsAToolOffAndBackOn_AndSaysWhatItDid()
    {
        var b = await Build(32_768, [("tracker", Small)]);

        var off = Tools(b, "off", "run_command");
        Assert.True(off.SaveConfig);
        Assert.Equal("run_command", b.Config.DisabledTools);
        Assert.Equal(Strings.SlashToolsTurnedOff(1, "`run_command`"), off.Message);
        Assert.Null(off.SessionTools);

        var again = Tools(b, "off", "run_command");
        Assert.False(again.SaveConfig);
        Assert.Equal(Strings.SlashToolsAlreadyOff(1, "`run_command`"), again.Message);

        // By the server's own name, and by the name the model calls: written as /tools shows it.
        Assert.True(Tools(b, "off", "list_issues,mcp__tracker__create_issue").SaveConfig);
        Assert.Equal("run_command\ntracker/list_issues\ntracker/create_issue", b.Config.DisabledTools);

        var back = Tools(b, "on", "run_command", "tracker/list_issues");
        Assert.True(back.SaveConfig);
        Assert.Equal("tracker/create_issue", b.Config.DisabledTools);
        Assert.Equal(Strings.SlashToolsTurnedOn(2, "`run_command`, `tracker/list_issues`"), back.Message);
    }

    [Fact]
    public async Task TheToolsCommand_RefusesWhatItCannotDo_AndWritesNothing()
    {
        var b = await Build(32_768, [("one", Small), ("two", Small)]);

        Assert.Equal(Strings.SlashToolsUnknown("nope"), Tools(b, "off", "nope").Message);
        Assert.Equal(Strings.SlashToolsSearchStaysOn, Tools(b, "off", "tool_search").Message);
        var ambiguous = Tools(b, "off", "ping");
        Assert.Equal(Strings.SlashToolsAmbiguous("ping", "`one/ping`, `two/ping`"), ambiguous.Message);
        // A sub-command it does not know is named, never taken for the list.
        Assert.Equal(Strings.SlashUsage(ToolsCommandHandler.Syntax), Tools(b, "disable", "run_command").Message);
        Assert.Equal(Strings.SlashUsage(ToolsCommandHandler.Syntax), Tools(b, "off", ",").Message);   // never an empty answer
        Assert.Equal(string.Empty, b.Config.DisabledTools);

        // Without a name: the conversation's switch, as before — nothing written.
        var session = Tools(b, "off");
        Assert.False(session.SessionTools);
        Assert.False(session.SaveConfig);
        Assert.Equal(string.Empty, b.Config.DisabledTools);
    }

    [Fact]
    public async Task TheToolsList_SaysEachToolsWeightAndState()
    {
        var b = await Build(32_768, [("ide", Heavy(93)), ("tracker", Small)]);
        b.Config.DisabledTools = "run_command";

        var list = Tools(b).Message;
        Assert.StartsWith(Strings.SlashToolsCurrent("on"), list);
        Assert.Contains("**" + Strings.ToolGroupBuiltIn + "**", list);
        Assert.Contains("**" + Strings.ToolGroupServer("ide") + "**", list);
        Assert.Contains("| `run_command` |", list);
        Assert.Contains(Strings.ToolStateOff, list);
        Assert.Contains(Strings.ToolStateOnDemand, list);
        Assert.Contains(Strings.ToolListHowTo, list);

        // The total is weighed on the definitions the rows say are sent, whatever the conversation's switch.
        var sent = ToolListPresenter.Build(b.Tools);
        Assert.Contains(Strings.ToolListSent(b.Tools.Definitions.Count, SettingsWidgets.Amount(RequestSize.ToolTokens(b.Tools.Definitions))),
                        sent.Summary);
        Assert.Equal(b.Tools.Definitions.Count, sent.Groups.Sum(g => g.Rows.Count(r => r.State == "sent")));

        var model = ToolListPresenter.Build(b.Tools.Profile(), sentTokens: 4_990);
        Assert.Contains(Strings.ToolListSent(model.Groups.Sum(g => g.Rows.Count(r => r.State == "sent")),
                                             SettingsWidgets.Amount(4_990)), model.Summary);
        Assert.Contains(Strings.ToolListTurnedOff(1), model.Summary);
        Assert.Contains(Strings.ToolListOnDemand(93), model.Summary);
        var search = model.Groups.SelectMany(g => g.Rows).Single(r => r.Name == "tool_search");
        Assert.False(search.CanToggle);
        var command = model.Groups.SelectMany(g => g.Rows).Single(r => r.Key == "run_command");
        Assert.True(command.Off);
        Assert.Equal(Strings.ToolTurnOn, command.ActionLabel);
        Assert.All(model.Groups.SelectMany(g => g.Rows), r => Assert.NotEqual("0", r.Weight));   // every definition weighs
    }

    [Fact]
    public async Task XRay_NamesTheToolsTurnedOff_InTheUsersLanguage()
    {
        var b = await Build(32_768, [("tracker", Small)]);
        Assert.Empty(Services.Agent.ContextManager.NextTurnTurnedOff(b.Tools, toolsEnabled: true));   // reference arm
        Assert.Null(XRayPanelPresenter.TurnedOffLine([]));

        b.Config.DisabledTools = "run_command\ntracker/ping";
        var off = Services.Agent.ContextManager.NextTurnTurnedOff(b.Tools, toolsEnabled: true);
        Assert.Equal(["run_command", "tracker/ping"], off);
        Assert.Empty(Services.Agent.ContextManager.NextTurnTurnedOff(b.Tools, toolsEnabled: false));

        var line = Strings.XrayToolsTurnedOff(2, "run_command, tracker/ping");
        Assert.Equal(line, XRayPanelPresenter.Build([], null, 0, 32_768, turnedOff: off).ToolsTurnedOff);
        Assert.Contains("- " + line, XRayCommandHandler.Handle([], 0, 32_768, new InferpalConfig(), turnedOff: off));
    }

    [Fact]
    public async Task ALineThatNamesNoToolHere_IsListed_AndTurnedBackOnByItsText()
    {
        // A server stopped, a tool only the other editor has: its line still says "off", so it is shown and can be lifted.
        var b = await Build(32_768, [("tracker", Small)]);
        b.Config.DisabledTools = "ghost_tool\nrider/get_file_problems\ntracker/ping";
        Assert.Equal(["ghost_tool", "rider/get_file_problems"], b.Tools.TurnedOffElsewhere());

        var model = ToolListPresenter.Build(b.Tools);
        var group = model.Groups.Single(g => g.Title == Strings.ToolGroupNotHere);
        Assert.Equal(["ghost_tool", "rider/get_file_problems"], group.Rows.Select(r => r.Key));
        Assert.All(group.Rows, r => Assert.True(r.CanToggle && r.Off));
        Assert.Contains(Strings.ToolListTurnedOff(3), model.Summary);

        var on = Tools(b, "on", "rider/get_file_problems");
        Assert.True(on.SaveConfig);
        Assert.Equal(Strings.SlashToolsTurnedOn(1, "`rider/get_file_problems`"), on.Message);
        Assert.Equal("ghost_tool\ntracker/ping", b.Config.DisabledTools);
        // Reference arm: a name nothing has, turned off, is still refused — there is nothing to turn off.
        Assert.Equal(Strings.SlashToolsUnknown("nope"), Tools(b, "off", "nope").Message);
    }

    [Fact]
    public async Task AServerNameWithASpace_IsDesignatedByTheNameTheModelCalls()
    {
        // `/tools on My Server/ping` splits into two names: the list and the refusal give the name that can be typed.
        var b = await Build(32_768, [("My Server", Small)]);
        var row = ToolListPresenter.Build(b.Tools).Groups.SelectMany(g => g.Rows).Single(r => r.Name == "mcp__My_Server__ping");
        Assert.Equal("mcp__My_Server__ping", row.Key);

        Assert.True(Tools(b, "off", "mcp__My_Server__ping").SaveConfig);
        Assert.Equal("My Server/ping", b.Config.DisabledTools);
        Assert.Contains("/tools on mcp__My_Server__ping", await Services.Agent.AgentOrchestrator.ExecuteToolSafeAsync(
            b.Tools, "mcp__My_Server__ping", NoArgs, CancellationToken.None));
        Assert.True(Tools(b, "on", "mcp__My_Server__ping").SaveConfig);
        Assert.Equal(string.Empty, b.Config.DisabledTools);
    }

    [Fact]
    public async Task AMalformedCallToAToolTurnedOff_IsRefusedAsTurnedOff_NotSentBackToBeRewritten()
    {
        var b = await Build(32_768, []);
        b.Config.DisabledTools = "run_command";
        var broken = new Models.ToolCallFunction("run_command", NoArgs) { BrokenShape = "text went on after the arguments object had closed" };
        var said = await Services.Agent.AgentOrchestrator.ExecuteToolSafeAsync(b.Tools, broken, replyCut: false, CancellationToken.None);
        Assert.Contains("turned off by the user", said);
        Assert.DoesNotContain("send the call again", said);
    }

    [Fact]
    public async Task AHandRename_PointsToRenameSymbol_OnlyWhileItIsOffered()
    {
        var b = await Build(32_768, []);
        var applyDiff = (Services.Tools.ApplyDiffTool)b.Tools.BuiltInTools.Single(t => t.Name == "apply_diff");
        var applyEdits = (Services.Tools.ApplyEditsTool)b.Tools.BuiltInTools.Single(t => t.Name == "apply_edits");
        Assert.True(applyDiff.RenameOffered!());   // reference arm
        b.Config.DisabledTools = "rename_symbol";
        Assert.False(applyDiff.RenameOffered!());
        Assert.False(applyEdits.RenameOffered!());

        var root = Path.Combine(Path.GetTempPath(), "inferpal-31b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cart = Path.Combine(root, "Cart.cs");
            File.WriteAllText(cart, "class Cart { public decimal ComputeTotal() => 0; }\n");
            File.WriteAllText(Path.Combine(root, "Receipt.cs"), "class R { decimal F(Cart c) => c.ComputeTotal(); }\n");
            foreach (var offered in new[] { true, false })
            {
                var tool = new Services.Tools.ApplyDiffTool(new NoopApproval(), new FileHistoryService(), () => root) { RenameOffered = () => offered };
                var refused = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new
                {
                    path = cart, old_content = "decimal ComputeTotal(int x)", new_content = "decimal CalculateTotal(int x)",
                }), CancellationToken.None);
                Assert.Equal(offered, refused.Contains("rename_symbol("));
            }
            var stale = Services.Tools.RenameIntent.StaleNote(("ComputeTotal", "CalculateTotal"), root, cart, renameOffered: false);
            Assert.Contains("`ComputeTotal` still appears in", stale);   // the fact stays
            Assert.DoesNotContain("rename_symbol", stale);               // the remedy goes with the tool
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void WritingTheSetting_KeepsTheOtherLines_AndRemovesEveryLineThatNamesTheTool()
    {
        var tool = new Services.Tools.ListFilesTool(() => null);
        Assert.Equal("# kept\nrun_command\nlist_files", ToolSwitches.Write("# kept\nrun_command", [tool], off: true));
        Assert.Equal("# kept\nrun_command", ToolSwitches.Write("# kept\nlist_files\r\nrun_command\nlist_files", [tool], off: false));
        Assert.True(ToolSwitches.Parse("  list_files  ").TurnsOff(tool));
        Assert.False(ToolSwitches.Parse("# list_files").TurnsOff(tool));   // a comment turns nothing off
    }
}
