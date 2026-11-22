using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Docs;
using Inferpal.Services.Documents;
using Inferpal.Services.Editor;
using Inferpal.Services.Execution;
using Inferpal.Services.Governance;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A run that changed the interface of an app it started looks at it before it says it is done (pari 160): asked once,
/// only while the app runs, only for what was written after its last look — a look being a picture brought back.
/// </summary>
public sealed class InterfaceLookTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-look-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var o in _owned) o.Dispose();
        ContentExclusions.ForgetForTests(_root);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private ToolRegistry Registry()
    {
        var config = new InferpalConfig();
        var client = new FakeInferenceProvider();
        var editor = new NullEditorSurface();
        var approval = new NoopApproval();
        var index = new ProjectIndexService(client, config, new LspSemanticProvider());
        _owned.Add(index);
        index.SetRoot(_root);
        return new ToolRegistry(editor, approval, config, index, client, new ProjectMapService(editor),
                                new McpToolService(config, approval), new DocsIndexService(client, config),
                                new OpenDocumentOverlay(), new NullDebugSession());
    }

    private static JsonElement Args(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement;

    private async Task ChangeAsync(FileHistoryService history, string rel, string content)
    {
        var path = Path.Combine(_root, rel);
        var (saved, _) = await history.BackUpBeforeChangeAsync(path, CancellationToken.None);
        Assert.True(saved);
        await File.WriteAllTextAsync(path, content);
    }

    private static string LongCommand => OperatingSystem.IsWindows() ? "ping -n 60 127.0.0.1" : "sleep 60";

    [Fact]
    public async Task AnInterfaceChange_WhileTheAppRuns_IsToBeLookedAt_AndAPictureBroughtBackSeesIt()
    {
        var tools = Registry();
        using (tools.History.BeginRunScope())
        {
            await ChangeAsync(tools.History, "app.css", "button { color: red; }\n");
            await ChangeAsync(tools.History, "server.py", "print('x')\n");
            Assert.Null(tools.UnseenInterfaceChange());   // reference arm: no app running, nothing to look at

            var started = await tools.ExecuteAsync("run_command", Args(new { command = LongCommand, background = true }), CancellationToken.None);
            var id = System.Text.RegularExpressions.Regex.Match(started, @"bg\d+").Value;
            Assert.False(string.IsNullOrEmpty(id), started);
            try
            {
                var unseen = tools.UnseenInterfaceChange();
                Assert.NotNull(unseen);
                Assert.Contains("You changed app.css,", unseen, StringComparison.Ordinal);
                Assert.DoesNotContain("server.py", unseen, StringComparison.Ordinal);   // not what a person sees
                Assert.Contains($"job {id} `", unseen, StringComparison.Ordinal);

                // A capture that brings nothing back is no look.
                File.WriteAllText(Path.Combine(_root, "page.html"), "<link rel=stylesheet href=app.css><button>Buy</button>");
                var chromium = PageCapture.FindAll().Any(b => b.Engine == CaptureEngine.Chromium);
                var looked = await tools.ExecuteAsync("view_page", Args(new { path = "page.html", width = 800 }), CancellationToken.None);
                if (looked.Contains("captured by", StringComparison.Ordinal))
                {
                    Assert.Null(tools.UnseenInterfaceChange());
                    await Task.Delay(50);
                    await ChangeAsync(tools.History, "app.css", "button { color: blue; }\n");   // written after the look
                    Assert.Contains("app.css", tools.UnseenInterfaceChange(), StringComparison.Ordinal);
                }
                else Assert.False(OperatingSystem.IsWindows() && chromium, looked);   // undecided only without a browser
            }
            finally
            {
                await tools.ExecuteAsync("run_command", Args(new { action = "stop", id }), CancellationToken.None);
            }
        }
    }

    /// <summary>A registry whose one tool edits app.css and whose look tool brings the change into view.</summary>
    private sealed class Interface(bool offersLook = true) : IToolRegistry
    {
        private bool _changed, _looked;
        public IReadOnlyList<ToolDefinition> Definitions => offersLook ? WithLook : [WithLook[0]];
        private static readonly ToolDefinition[] WithLook =
            [new("function", new ToolFunction("apply_diff", "edit", new { })), new("function", new ToolFunction("view_page", "look", new { }))];
        public DiffInfo? ConsumeDiff() => null;
        public int? WritesInRun => _changed ? 1 : 0;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        {
            if (name == "apply_diff") { _changed = true; _looked = false; }
            if (name == "view_page") _looked = true;
            return Task.FromResult(name == "view_page" ? "captured: the button is hidden behind the banner" : "applied");
        }
        public string? UnseenInterfaceChange() =>
            _changed && !_looked ? "You changed app.css, and the app you started is still running: job bg1 `npm run dev`." : null;
    }

    private static ChatTurnResult Call(string name) =>
        new(string.Empty, [new ToolCallDto(new ToolCallFunction(name, JsonDocument.Parse("""{"path":"app.css"}""").RootElement.Clone()))], 0, 0);

    private sealed class ScriptedOllama(IEnumerable<ChatTurnResult> script) : OllamaClient(new InferpalConfig { ContextWindowSize = 32768 })
    {
        private readonly Queue<ChatTurnResult> _script = new(script);
        public List<List<ChatMessageDto>> Seen { get; } = [];

        public override Task<ChatTurnResult> SendChatAsync(string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            ReasoningEffort effort, CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal, string? toolChoice = null,
            Action<string>? onThinking = null, Action<ModelWait?>? onWaiting = null)
        {
            Seen.Add([.. messages]);
            return Task.FromResult(_script.Count > 1 ? _script.Dequeue() : _script.Peek());
        }
    }

    private static int LookPrompts(IEnumerable<List<ChatMessageDto>> seen) =>
        seen.SelectMany(m => m).Where(m => m.Content.StartsWith("Before you answer: You changed app.css", StringComparison.Ordinal))
            .Select(m => m.Content).Distinct().Count();

    /// <summary>In a chat turn: asked once, it looks, and the answer after the look is the one kept.</summary>
    [Fact]
    public async Task AChatRun_LooksOnce_AndAnswersAfterTheLook()
    {
        var client = new ScriptedOllama(
        [
            Call("apply_diff"),
            new("The button is blue now.", null, 0, 0),
            Call("view_page"),
            new("The button was hidden behind the banner; I moved it.", null, 0, 0),
        ]);
        AgentResult result;
        using (Inferpal.Services.Agent.RunDiffReview.Enter(() => { }))
            result = await client.RunAgentAsync("m", [new("user", "Make the buy button blue.")], new Interface(), _ => { }, null, CancellationToken.None);

        Assert.Equal(1, LookPrompts(client.Seen));
        Assert.Contains("hidden behind the banner", result.FinalResponse, StringComparison.Ordinal);
    }

    private sealed class ScriptedChatClient(IEnumerable<ChatTurnResult> script) : IChatTurnClient
    {
        private readonly Queue<ChatTurnResult> _script = new(script);
        public List<List<ChatMessageDto>> Seen { get; } = [];

        public Task<ChatTurnResult> SendChatAsync(string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal, string? toolChoice = null, Action<string>? onThinking = null)
        {
            Seen.Add([.. messages]);
            return Task.FromResult(_script.Count > 1 ? _script.Dequeue() : _script.Peek());
        }
    }

    /// <summary>In Agent mode: asked once, before the diff review, and the answer after the look is the one kept.</summary>
    [Fact]
    public async Task AnAgentRun_LooksOnce_BeforeItAnswers()
    {
        var client = new ScriptedChatClient(
        [
            new("""{"goal":"blue button","steps":[{"i":1,"desc":"edit app.css"}]}""", null, 0, 0),
            Call("apply_diff"),
            new("The button is blue now.", null, 0, 0),
            Call("view_page"),
            new("The button was hidden behind the banner; I moved it.", null, 0, 0),
        ]);
        var result = await new AgentOrchestrator(client, new InferpalConfig { ContextWindowSize = 32768, CompactionEnabled = false, AgentMaxIterations = 10, AgentModeEnabled = true })
            .RunAsync(model: "m", history: [new("system", "s"), new("user", "Make the buy button blue.")], tools: new Interface(),
                      onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: null, onToolExecuted: null, onStreamReset: null,
                      ct: CancellationToken.None);

        Assert.Equal(1, LookPrompts(client.Seen));
        Assert.Contains("hidden behind the banner", result.FinalResponse, StringComparison.Ordinal);
    }

    /// <summary>A turn that sends no look tool (a small window cut it, a mode, a tool turned off) is never asked to look: it
    /// could only be refused, or answer it looked without looking.</summary>
    [Fact]
    public async Task ATurnThatOffersNoLookTool_IsNotAsked()
    {
        var client = new ScriptedOllama([Call("apply_diff"), new("The button is blue now.", null, 0, 0)]);
        using (Inferpal.Services.Agent.RunDiffReview.Enter(() => { }))
            await client.RunAgentAsync("m", [new("user", "Make the buy button blue.")], new Interface(offersLook: false), _ => { }, null, CancellationToken.None);

        Assert.Equal(0, LookPrompts(client.Seen));
    }

    /// <summary>A model that reasons often ends on a turn with no visible text: the run is asked to look all the same, and
    /// that empty turn is never kept as the answer.</summary>
    [Fact]
    public async Task AnAgentRunThatEndsOnReasoningAlone_IsAskedToLookToo()
    {
        var client = new ScriptedChatClient(
        [
            new("""{"goal":"blue button","steps":[{"i":1,"desc":"edit app.css"}]}""", null, 0, 0),
            Call("apply_diff"),
            new("<think>The edit is done.</think>", null, 0, 0),
            Call("view_page"),
            new("The button was hidden behind the banner; I moved it.", null, 0, 0),
        ]);
        var result = await new AgentOrchestrator(client, new InferpalConfig { ContextWindowSize = 32768, CompactionEnabled = false, AgentMaxIterations = 10, AgentModeEnabled = true })
            .RunAsync(model: "m", history: [new("system", "s"), new("user", "Make the buy button blue.")], tools: new Interface(),
                      onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: null, onToolExecuted: null, onStreamReset: null,
                      ct: CancellationToken.None);

        Assert.Equal(1, LookPrompts(client.Seen));
        Assert.Contains("hidden behind the banner", result.FinalResponse, StringComparison.Ordinal);
    }

    /// <summary>Reference arm: a run that looked already is not asked to look again.</summary>
    [Fact]
    public async Task ARunThatLookedAfterItsChange_IsNotAsked()
    {
        var client = new ScriptedOllama(
        [
            Call("apply_diff"),
            Call("view_page"),
            new("The button is blue, and I checked it on the page.", null, 0, 0),
        ]);
        using (Inferpal.Services.Agent.RunDiffReview.Enter(() => { }))
            await client.RunAgentAsync("m", [new("user", "Make the buy button blue.")], new Interface(), _ => { }, null, CancellationToken.None);

        Assert.Equal(0, LookPrompts(client.Seen));
    }
}
