using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Presentation;
using Inferpal.Services.Tasks;
using Inferpal.Services.Docs;
using Inferpal.Services.Execution;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ An editor that hands the agent its own MCP server can fill the window before the first word (Rider: 93 tools,
/// ~49 000 tokens as a request counts them, more than a 32K window — the first question refused). Beyond a tenth of the window in use, the
/// heaviest servers wait on demand: <c>tool_search</c> names their tools, finds them by keyword or name and loads them,
/// declared after every other definition, in load order, never moved.
/// </summary>
[Collection(SignalCollection.Name)]
public sealed class McpToolsOnDemandTests : IDisposable
{
    private readonly SignalScratchDir _scratch = new();
    private readonly List<IDisposable> _disposables = [];
    private readonly List<IAsyncDisposable> _asyncDisposables = [];

    public McpToolsOnDemandTests() => TestRagStore.Redirect();

    public void Dispose()
    {
        foreach (var d in _asyncDisposables) d.DisposeAsync().AsTask().GetAwaiter().GetResult();
        foreach (var d in _disposables) d.Dispose();
        _scratch.Dispose();
    }

    /// <summary>A server that lists the given tools and counts the calls it receives.</summary>
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
        properties = new
        {
            path = new { type = "string", description = "The file, relative to the project." },
            line = new { type = "integer", description = "A line number in that file." },
        },
        required = new[] { "path" },
    });

    /// <summary>A heavy server: <paramref name="count"/> tools whose definitions weigh about what an IDE's do.</summary>
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
        new("ping_tracker", "Checks that the issue tracker answers.", Schema),
    ];

    /// <summary>An approval that records what it is asked and answers <paramref name="grant"/>.</summary>
    private sealed class RecordingApproval(bool grant = true) : IApprovalService
    {
        public List<string> Asked { get; } = [];
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               Services.CodeActions.DiffInfo? diff = null, bool forcePrompt = false)
        {
            Asked.Add(toolName);
            return Task.FromResult(grant);
        }
    }

    private Task<(ToolRegistry Tools, Dictionary<string, ListingClient> Clients)> Build(
        int? window, params (string Server, IReadOnlyList<McpToolInfo> Tools)[] servers) => Build(window, new NoopApproval(), servers);

    private async Task<(ToolRegistry Tools, Dictionary<string, ListingClient> Clients)> Build(
        int? window, IApprovalService approval, params (string Server, IReadOnlyList<McpToolInfo> Tools)[] servers)
    {
        var clients = servers.ToDictionary(s => s.Server, s => new ListingClient(s.Server, s.Tools));
        var json = "{" + string.Join(",", servers.Select(s => $"\"{s.Server}\": {{ \"command\": \"x\" }}")) + "}";
        var config = new InferpalConfig { RagEnabled = false, McpEnabled = true, McpServersJson = json };
        var mcp = new McpToolService(config, approval, cfg => clients[cfg.Name], [TimeSpan.FromMilliseconds(5)]);
        _asyncDisposables.Add(mcp);
        await mcp.RefreshAsync();
        var client = new FakeInferenceProvider();
        var index = new ProjectIndexService(client, config, new LspSemanticProvider());
        _disposables.Add(index);
        var editor = new NullEditorSurface();
        var tools = new ToolRegistry(editor, approval, config, index, client, new ProjectMapService(editor, index), mcp,
                                     new DocsIndexService(client, config), new OpenDocumentOverlay(), new NullDebugSession());
        _disposables.Add(tools);
        if (window is { } w) tools.WindowInUse = () => w;
        return (tools, clients);
    }

    private static List<string> Names(ToolRegistry tools) => [.. tools.Definitions.Select(d => d.Function.Name)];

    private static Task<string> Search(ToolRegistry tools, string query) =>
        tools.ExecuteAsync("tool_search", JsonSerializer.SerializeToElement(new { query }), CancellationToken.None);

    [Fact]
    public async Task UnderTheBudget_EveryMcpToolIsDeclared_AndToolSearchIsNotOffered()
    {
        // Reference arm: a small server in a 32K window costs nothing worth deferring.
        var (tools, _) = await Build(32_768, ("tracker", Small));
        var names = Names(tools);
        Assert.Contains("mcp__tracker__list_issues", names);
        Assert.Contains("mcp__tracker__create_issue", names);
        Assert.DoesNotContain("tool_search", names);
        Assert.Contains("read_file", names);   // witness: the whole list was read
    }

    [Fact]
    public async Task BeyondTheBudget_TheHeaviestServerWaits_TheSmallOneStaysDeclared()
    {
        var (tools, _) = await Build(32_768, ("ide", Heavy(93)), ("tracker", Small));
        var names = Names(tools);
        Assert.Contains("mcp__tracker__list_issues", names);
        Assert.DoesNotContain(names, n => n.StartsWith("mcp__ide__", StringComparison.Ordinal));
        Assert.Contains("tool_search", names);

        var description = tools.Definitions.Single(d => d.Function.Name == "tool_search").Function.Description;
        Assert.Contains("server 'ide' (93 tools)", description);
        Assert.Contains("mcp__ide__tool_001", description);
        Assert.Contains("mcp__ide__tool_093", description);
        Assert.DoesNotContain("tracker", description);   // a declared server is not on demand
    }

    [Fact]
    public async Task ASearch_LoadsWhatItFound_AtTheEnd_AndASecondLoadMovesNothing()
    {
        var (tools, _) = await Build(32_768, ("ide", Heavy(93)), ("tracker", Small));
        var before = Names(tools);

        var said = await Search(tools, "select:mcp__ide__tool_042");
        Assert.Contains("mcp__ide__tool_042", said);
        Assert.Contains("Parameters:", said);          // the schema comes with it
        Assert.Contains("\"path\"", said);
        var once = Names(tools);
        Assert.Equal([.. before, "mcp__ide__tool_042"], once);

        // By its name on the server, without the prefix: the same tool, after the first, nothing before it moved.
        said = await Search(tools, "select:tool_007");
        Assert.Contains("mcp__ide__tool_007", said);
        Assert.Equal([.. before, "mcp__ide__tool_042", "mcp__ide__tool_007"], Names(tools));

        // Again: said, nothing changes.
        Assert.StartsWith("Already loaded", await Search(tools, "select:mcp__ide__tool_042"));
        Assert.Equal([.. before, "mcp__ide__tool_042", "mcp__ide__tool_007"], Names(tools));
    }

    [Fact]
    public async Task AKeywordSearch_LoadsAtMostFive_RankedByTheWordsFound()
    {
        var tools0 = Heavy(20);
        tools0[13] = tools0[13] with { Name = "get_run_configurations", Description = "Returns the run configurations of the solution." };
        var (tools, _) = await Build(32_768, ("ide", tools0));

        var said = await Search(tools, "run configurations");
        Assert.StartsWith("Loaded 1 tool", said);
        Assert.Contains("mcp__ide__get_run_configurations", said);

        // A word every tool has: five, never twenty.
        said = await Search(tools, "environment");
        Assert.StartsWith("Loaded 5 tools", said);
        Assert.Equal(6, Names(tools).Count(n => n.StartsWith("mcp__ide__", StringComparison.Ordinal)));

        Assert.StartsWith("No tool on demand that is not loaded yet matches", await Search(tools, "kubernetes"));
    }

    [Fact]
    public async Task AKeywordSearch_NamesTheOtherMatches_AndLoadsOnlyTheClosest()
    {
        var tools0 = Heavy(20);
        tools0[2] = tools0[2] with { Name = "execute_run_configuration", Description = "Runs a run configuration." };
        tools0[3] = tools0[3] with { Name = "get_run_configurations", Description = "Returns the run configurations." };
        var (tools, _) = await Build(32_768, ("ide", tools0));

        // "run" is in both names, "configurations" in one: the closest is loaded, the other is named to be selected.
        var said = await Search(tools, "run configurations");
        Assert.StartsWith("Loaded 1 tool", said);
        Assert.Contains("mcp__ide__get_run_configurations", said);
        Assert.Contains("1 more match", said);
        Assert.Contains("mcp__ide__execute_run_configuration", said);
        Assert.DoesNotContain("mcp__ide__execute_run_configuration", Names(tools));

        // A loaded tool is not found again: the next search reaches the ones still waiting.
        said = await Search(tools, "environment");
        Assert.StartsWith("Loaded 5 tools", said);
        said = await Search(tools, "environment");
        Assert.StartsWith("Loaded 5 tools", said);   // five more, never "already loaded"
        Assert.Equal(11, Names(tools).Count(n => n.StartsWith("mcp__ide__", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SelectingMoreThanFive_LoadsFive_AndNamesTheRestAsNotLoadedYet()
    {
        var (tools, _) = await Build(32_768, ("ide", Heavy(93)));
        var said = await Search(tools, "select:tool_001,tool_002,tool_003,tool_004,tool_005,tool_006,tool_007");
        Assert.StartsWith("Loaded 5 tools", said);
        Assert.Contains("Not loaded yet — at most 5 per call", said);
        Assert.Contains("mcp__ide__tool_006, mcp__ide__tool_007", said);
        Assert.DoesNotContain("No tool on demand is named", said);   // they exist: the cap is said, never a miss
        Assert.DoesNotContain("mcp__ide__tool_006", Names(tools));
    }

    [Fact]
    public async Task ANameTwoServersShare_IsSaidAmbiguous_WithBothFullNames()
    {
        // A small window: both servers on demand.
        var (tools, _) = await Build(8_192, ("ide", Heavy(40)), ("ide2", Heavy(30)));
        var said = await Search(tools, "select:tool_001");
        Assert.Contains("'tool_001' names several tools on demand: mcp__ide2__tool_001, mcp__ide__tool_001", said);   // ordinal
        Assert.DoesNotContain("No tool on demand is named", said);
        Assert.DoesNotContain("mcp__ide__tool_001", Names(tools));

        Assert.Contains("No tool on demand is named 'tool_999'", await Search(tools, "select:tool_999"));
    }

    [Fact]
    public async Task AToolCalledByItsName_IsApproved_RunsAndIsLoaded()
    {
        var approval = new RecordingApproval();
        var (tools, clients) = await Build(32_768, approval, ("ide", Heavy(93)));
        var result = await tools.ExecuteAsync("mcp__ide__tool_010", JsonSerializer.SerializeToElement(new { path = "a.cs" }),
                                              CancellationToken.None);
        Assert.Contains("done", result);
        Assert.Equal(["mcp__ide__tool_010"], approval.Asked);   // the approval of every MCP call, before it runs
        Assert.Equal(["tool_010"], clients["ide"].Calls);
        Assert.Equal("mcp__ide__tool_010", Names(tools)[^1]);
    }

    [Fact]
    public async Task ACallWrittenAsText_MayNameAToolOnDemand_ThroughEveryPassThroughDecorator()
    {
        // The clients recognize a call written as text by these names: a tool read in tool_search's description is one.
        var (tools, _) = await Build(32_768, ("ide", Heavy(93)));
        Assert.DoesNotContain("mcp__ide__tool_050", Names(tools));
        Assert.Contains("mcp__ide__tool_050", tools.CallableNames);
        Assert.Contains("mcp__ide__tool_050", ((IToolRegistry)new StepModeToolRegistry(tools, _ => Task.CompletedTask)).CallableNames);
        // A decorator that sends no MCP tool does not make them callable.
        Assert.DoesNotContain("mcp__ide__tool_050", ((IToolRegistry)new PlanModeToolRegistry(tools)).CallableNames);
    }

    [Fact]
    public async Task PlanModeAndBackgroundTasks_OfferNoToolSearch()
    {
        var (tools, _) = await Build(32_768, ("ide", Heavy(93)));
        Assert.Contains("tool_search", Names(tools));   // witness: offered outside them
        Assert.DoesNotContain(new PlanModeToolRegistry(tools).Definitions, d => d.Function.Name == "tool_search");
        Assert.DoesNotContain(new BackgroundTaskToolRegistry(tools, currentRoot: () => null).Definitions,
                              d => d.Function.Name == "tool_search");
    }

    [Fact]
    public async Task ASiblingRegistry_SharesTheLoadedTools()
    {
        // /tdd writes through a sibling: the conversation's loaded tools are there, and what it loads stays.
        var (tools, _) = await Build(32_768, ("ide", Heavy(93)));
        await Search(tools, "select:tool_001");
        using var sibling = tools.WithApprovalService(new NoopApproval());
        Assert.Equal("mcp__ide__tool_001", Names(sibling)[^1]);
        await Search(sibling, "select:tool_002");
        Assert.Equal(["mcp__ide__tool_001", "mcp__ide__tool_002"], Names(tools).TakeLast(2));
    }

    [Fact]
    public async Task AWindowThatChanges_PutsALoadedToolBackInItsServer_ThenAtTheEndAgain()
    {
        var window = 32_768;
        var (tools, _) = await Build(null, ("ide", Heavy(93)));
        tools.WindowInUse = () => window;
        await Search(tools, "select:tool_050");
        Assert.Equal("mcp__ide__tool_050", Names(tools)[^1]);

        window = int.MaxValue / 8;   // everything fits: the server is declared, in its own order
        var all = Names(tools);
        Assert.DoesNotContain("tool_search", all);
        Assert.Equal("mcp__ide__tool_093", all[^1]);

        window = 32_768;             // on demand again: still loaded, at the end
        Assert.Equal("mcp__ide__tool_050", Names(tools)[^1]);
    }

    [Fact]
    public async Task InASmallWindow_TheNamesShareTheBudget_AndACutServerIsNamedWithItsCount()
    {
        var (tools, _) = await Build(8_192, ("ide", Heavy(300)));
        var description = tools.Definitions.Single(d => d.Function.Name == "tool_search").Function.Description;
        Assert.Contains("server 'ide': 300 tools, not listed here — search them by keyword", description);
        Assert.DoesNotContain("mcp__ide__tool_001", description);
        Assert.True(description.Length < 819 * 4, $"{description.Length}");   // within the 10 % of an 8K window
    }

    [Fact]
    public async Task XRay_SaysWhichToolsWait_InTheUsersLanguage()
    {
        var (tools, _) = await Build(32_768, ("ide", Heavy(93)));
        await Search(tools, "select:tool_001");
        var waiting = Services.Agent.ContextManager.NextTurnToolsOnDemand(tools, toolsEnabled: true, planMode: false);
        Assert.Equal([("ide", 92)], waiting);
        Assert.Equal(Strings.XrayToolsOnDemand(92, "ide (92)"), XRayPanelPresenter.ToolsOnDemandLine(waiting));
        Assert.Empty(Services.Agent.ContextManager.NextTurnToolsOnDemand(tools, toolsEnabled: true, planMode: true));
        Assert.Empty(Services.Agent.ContextManager.NextTurnToolsOnDemand(tools, toolsEnabled: false, planMode: false));

        var model = XRayPanelPresenter.Build([], null, 0, 32_768, toolsOnDemand: waiting);
        Assert.Equal(Strings.XrayToolsOnDemand(92, "ide (92)"), model.ToolsOnDemand);
        var markdown = Services.Commands.XRayCommandHandler.Handle([], 0, 32_768, new InferpalConfig(), toolsOnDemand: waiting);
        Assert.Contains("- " + Strings.XrayToolsOnDemand(92, "ide (92)"), markdown);
        // Reference arm: nothing waits, nothing is said (the line names tool_search in every language).
        Assert.Null(XRayPanelPresenter.ToolsOnDemandLine([]));
        Assert.DoesNotContain("tool_search", Services.Commands.XRayCommandHandler.Handle([], 0, 32_768, new InferpalConfig()));
    }

    [Fact]
    public void EveryXRayOfBothEditors_PassesTheToolsOnDemand()
    {
        // The site that drops the argument stays green everywhere else: the line silently disappears from that X-Ray.
        var root = Repo();
        var sites = 0;
        var missing = new List<string>();
        foreach (var file in new[] { "Inferpal", "Inferpal.Host" }.SelectMany(d => Directory.EnumerateFiles(
                     Path.Combine(root, d), "*.cs", SearchOption.AllDirectories))
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(File.ReadAllText(file));
            foreach (var call in tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>())
            {
                if (call.Expression is not Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax m) continue;
                var target = $"{m.Expression}.{m.Name}";
                if (target is not ("XRayPanelPresenter.Build" or "XRayCommandHandler.Handle")) continue;
                sites++;
                if (!call.ArgumentList.Arguments.Any(a => a.NameColon?.Name.Identifier.Text == "toolsOnDemand"))
                    missing.Add($"{Path.GetRelativePath(root, file)}: {target}");
            }
        }
        Assert.True(sites >= 3, $"only {sites} X-Ray sites found: the scan reads nothing");   // VS panel, host panel, /xray
        Assert.Empty(missing);
    }

    [Fact]
    public void ReplacingTheConversation_ForgetsTheLoadedTools_InBothEditors()
    {
        // The funnels every conversation replacement goes through: VS's ResetTurnAccounting (rule 25 holds every site to
        // it), the host's ForgetConversationState (initialize, chat/reset, /clear, /template, session load and branch).
        var root = Repo();
        foreach (var (file, method) in new[]
                 {
                     (Path.Combine("Inferpal", "ToolWindow", "InferpalToolWindowData.UiHelpers.cs"), "ResetTurnAccounting"),
                     (Path.Combine("Inferpal.Host", "HostServer.cs"), "ForgetConversationState"),
                 })
        {
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, file)));
            var body = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
                           .Single(m => m.Identifier.Text == method);
            Assert.True(body.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
                            .Any(i => i.Expression is Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax
                                      { Name.Identifier.Text: "ForgetLoadedTools" }), $"{file}: {method}");
        }
    }

    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Inferpal.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("no Inferpal.sln above the test binaries");
    }

    [Fact]
    public async Task ANewConversation_ForgetsTheLoadedTools()
    {
        var (tools, _) = await Build(32_768, ("ide", Heavy(93)));
        await Search(tools, "select:tool_001");
        Assert.Contains("mcp__ide__tool_001", Names(tools));
        tools.ForgetLoadedTools();
        Assert.DoesNotContain("mcp__ide__tool_001", Names(tools));
        Assert.Contains("tool_search", Names(tools));
    }

    [Fact]
    public async Task AnIdesToolSet_At32K_SendsHalfTheDefinitionsAtMost_AndFits()
    {
        var (tools, _) = await Build(32_768, ("ide", Heavy(93)));
        var onDemand = RequestSize.ToolTokens(tools.Definitions);
        tools.WindowInUse = () => int.MaxValue / 8;   // every definition declared: what a request carried before
        var all = RequestSize.ToolTokens(tools.Definitions);

        Assert.True(all > 32_768, $"the reference set must not fit: {all}");
        Assert.True(onDemand <= all / 2, $"{onDemand} of {all}");
        Assert.True(onDemand < 32_768 / 2, $"{onDemand}");
    }

    [Fact]
    public async Task AnUnknownWindow_IsTakenAsTheMeasuredOne()
    {
        var (tools, _) = await Build(window: null, ("ide", Heavy(93)));
        Assert.Contains("tool_search", Names(tools));
        Assert.DoesNotContain(Names(tools), n => n.StartsWith("mcp__ide__", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheBuiltInsAreSentInTheSameOrder_WhetherToolSearchIsOfferedOrNot()
    {
        // The definitions sit near the head of a request: tool_search, registered last, never moves what comes before.
        var (declared, _) = await Build(32_768, ("tracker", Small));
        var (deferring, _) = await Build(32_768, ("ide", Heavy(93)));
        var a = Names(declared).TakeWhile(n => !n.StartsWith("mcp__", StringComparison.Ordinal)).ToList();
        var b = Names(deferring).TakeWhile(n => !n.StartsWith("mcp__", StringComparison.Ordinal)).ToList();
        Assert.Equal(a, b.Where(n => n != "tool_search"));
        Assert.Equal("tool_search", b[^1]);
    }
}
