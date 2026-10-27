using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Agent;
using Inferpal.Services.Commands;
using Inferpal.Services.Execution;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// How much a model reasons for a question and in an agent run: one setting each, read where the call is made. A
/// question (a <c>/check</c> review included, with tools or without) asks for the chat's effort, an agent run for the
/// agent's at every call of the run, and a code action keeps the server's — no measure says its edit survives a lower
/// one. LM Studio is sent a level only when the model publishes it.
/// </summary>
public sealed class ReasoningEffortSettingTests
{
    [Theory]
    [InlineData("low", "Low")]
    [InlineData(" Medium ", "Medium")]
    [InlineData("HIGH", "High")]
    [InlineData("off", "Off")]
    [InlineData("server", "Server")]
    [InlineData("", "Server")]
    [InlineData(null, "Server")]
    [InlineData("none", "Server")]   // the wire's word, not the setting's: read as the server's
    public void ASetting_ReadsAsTheLevelItNames_AndAnythingElseAsTheServers(string? value, string expected) =>
        Assert.Equal(Enum.Parse<ReasoningEffort>(expected), ReasoningEffortSetting.Parse(value));

    [Fact]
    public void AQuestion_ReasonsLow_AndAnAgentRun_AsTheServerSays_ByDefault()
    {
        var config = new InferpalConfig();

        Assert.Equal(ReasoningEffort.Low,    ReasoningEffortSetting.Chat(config));
        Assert.Equal(ReasoningEffort.Server, ReasoningEffortSetting.Agent(config));
    }

    [Fact]
    public void AQuestion_AsksTheChatsEffort_ACodeAction_TheServers()
    {
        var config = new InferpalConfig { ReasoningEffortChat = "high" };

        Assert.Equal(ReasoningEffort.High,   ChatTurnPolicy.TurnEffort(config, codeAction: false));
        Assert.Equal(ReasoningEffort.Server, ChatTurnPolicy.TurnEffort(config, codeAction: true));
    }

    [Theory]
    [InlineData("Inferpal/ToolWindow/InferpalToolWindowData.ChatTurn.cs", 1)]
    [InlineData("Inferpal.Host/HostServer.cs", 2)]
    public void EveryChatTurnCall_TakesItsEffortFromTheSharedDecision(string file, int atLeast)
    {
        var calls = ChatTurnBudgetParityTests.ModelCalls(file);
        // WITNESS: the turn's model calls are really read — a renamed method would leave nothing to judge, green.
        Assert.True(calls.Length >= atLeast, $"{file}: only {calls.Length} model call(s) read.");

        foreach (var call in calls)
            Assert.True(call.ArgumentList.ToString().Contains("ChatTurnPolicy.TurnEffort(", StringComparison.Ordinal),
                        $"{file}: a chat turn's model call asks for an effort of its own (or the server's, unasked): "
                        + call.ToString()[..Math.Min(120, call.ToString().Length)]);
    }

    [Fact]
    public void EveryAgentRun_NamesItsEffort()
    {
        // An agent run that names no effort takes the server's, silently: the setting the user chose is not asked.
        var root  = ConversationPersistenceSilenceTests.RepoRoot();
        var calls = new[] { "Inferpal.Core", "Inferpal", "Inferpal.Host" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f).GetRoot()
                .DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
                .Where(i => i.Expression is Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax m
                         && m.Name.Identifier.Text == "RunAgentAsync")
                .Select(i => (File: Path.GetFileName(f), Call: i)))
            .ToList();

        // WITNESS: the product's agent runs are really read (both front-ends' chat, /task, the fix loops).
        Assert.True(calls.Count >= 6, $"only {calls.Count} agent run(s) read");
        Assert.All(calls, c => Assert.True(
            c.Call.ArgumentList.Arguments.Any(a => a.NameColon?.Name.Identifier.Text == "effort"),
            $"{c.File}: an agent run names no effort: {c.Call.ToString()[..Math.Min(100, c.Call.ToString().Length)]}"));
    }

    /// <summary>Each chat turn's call, and the code-action flag it passes: a question is not a code action. The host's
    /// carry the mode the person picked, whose effort wins over the chat's.</summary>
    [Theory]
    [InlineData("Inferpal/ToolWindow/InferpalToolWindowData.ChatTurn.cs", "ChatTurnPolicy.TurnEffort(_config, codeAction: oneTimeModel is not null)")]
    [InlineData("Inferpal.Host/HostServer.cs", "ChatTurnPolicy.TurnEffort(s.Config, codeAction: false, mode)|ChatTurnPolicy.TurnEffort(s.Config, p.CodeAction, mode)")]
    public void EveryChatTurnCall_SaysWhetherItIsACodeAction(string file, string expected)
    {
        var efforts = ChatTurnBudgetParityTests.ModelCalls(file)
            .Select(c => c.ArgumentList.Arguments.Select(a => a.Expression.ToString())
                          .Single(e => e.StartsWith("ChatTurnPolicy.TurnEffort(", StringComparison.Ordinal)))
            .ToArray();

        Assert.Equal(expected.Split('|'), efforts);
    }

    // ── An agent run: one effort, the whole run ────────────────────────────────

    private sealed class OneTool : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } = [new("function", new ToolFunction("read_file", "reads", new { }))];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => Task.FromResult("class Cart {}");
    }

    [Fact]
    public async Task AnAgentRun_AsksTheAgentsEffort_AtEveryCall()
    {
        // The plan, the step that reads a file, an empty answer — and the synthesis that replaces it.
        var replies = new Queue<ChatTurnResult>(
        [
            new("""{"goal":"answer","steps":[{"id":1,"description":"Read Cart.cs","tool":"read_file"}]}""", null, 0, 0),
            new(string.Empty, [new ToolCallDto(new ToolCallFunction("read_file",
                    JsonDocument.Parse("""{"path":"Cart.cs"}""").RootElement.Clone()))], 0, 0),
            new(string.Empty, null, 0, 0),
            new("Cart holds the items.", null, 0, 0),
        ]);
        var client = new FakeInferenceProvider
        {
            OnChatRequest = (_, _, _, _) => Task.FromResult(replies.Count > 1 ? replies.Dequeue() : replies.Peek()),
        };
        var orchestrator = new AgentOrchestrator(client, new InferpalConfig
        {
            ReasoningEffortAgent = "medium", ReasoningEffortChat = "high", ContextWindowSize = 8192, CompactionEnabled = false,
        });

        var result = await orchestrator.RunAsync(
            model: "m", history: [new("system", "s"), new("user", "What does Cart do?")], tools: new OneTool(),
            onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: null,
            onToolExecuted: null, onStreamReset: null, ct: CancellationToken.None);

        // WITNESS: the synthesis ran — its answer is the run's — so its effort is among those asked.
        Assert.Equal("Cart holds the items.", result.FinalResponse);
        Assert.True(client.Efforts.Count >= 4, $"only {client.Efforts.Count} call(s) asked");
        Assert.All(client.Efforts, e => Assert.Equal(ReasoningEffort.Medium, e));
    }

    // ── The commands that run a question or an agent ───────────────────────────

    [Fact]
    public async Task ACheckReview_AsksTheChatsEffort()
    {
        var root = Directory.CreateTempSubdirectory("inferpal-effort-check-").FullName;
        try
        {
            // A review with a check, as most repositories that use /check have one.
            Directory.CreateDirectory(Path.Combine(root, ".inferpal", "checks"));
            File.WriteAllText(Path.Combine(root, ".inferpal", "checks", "secrets.md"),
                              "---\ndescription: secrets\n---\nNo secret may be committed.");
            const string diff = "diff --git a/src/Cart.cs b/src/Cart.cs\n--- a/src/Cart.cs\n+++ b/src/Cart.cs\n@@ -1,3 +1,3 @@\n"
                              + " class Cart {\n-    int Total(int a, int b) => a + b;\n+    int Total(int a, int b) => a - b;\n }\n";
            var client = new FakeInferenceProvider { ChatResult = new("Nothing to report.", [], 0, 0) };

            await CheckCommandHandler.HandleAsync(
                client, new InferpalConfig { ReasoningEffortChat = "medium", ReasoningEffortAgent = "high" }, root, ["/check"],
                (args, _) => Task.FromResult(args == "diff --staged" ? (diff, 0) : (string.Empty, 0)),
                onProgress: null, CancellationToken.None);

            Assert.Equal(ReasoningEffort.Medium, Assert.Single(client.Efforts));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ } }
    }

    private sealed class RedThenGreen : IToolRegistry
    {
        private readonly Queue<string> _outputs = new(
        [
            "✗ FAILED — Failed: 1, Passed: 4, Skipped: 0, Total: 5\n\nFailing tests:\n  ✗ MyTest",
            "✓ PASSED — Failed: 0, Passed: 5, Skipped: 0, Total: 5",
        ]);
        public IReadOnlyList<ToolDefinition> Definitions => [];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) =>
            Task.FromResult(_outputs.Count > 0 ? _outputs.Dequeue() : "✓ PASSED");
    }

    [Fact]
    public async Task ATddFixRun_AsksTheAgentsEffort()
    {
        var client = new FakeInferenceProvider { ChatResult = new("Fixed.", [], 0, 0) };

        await TddCommandHandler.HandleAsync(
            client, new InferpalConfig { DefaultModel = "big", ReasoningEffortAgent = "high", ReasoningEffortChat = "low" },
            new RedThenGreen(), systemPrompt: null, ["/tdd"], projectRoot: @"C:\proj",
            onProgress: null, onTestReport: null, onStep: null, onToken: null, onFixResult: null, CancellationToken.None);

        Assert.Equal(ReasoningEffort.High, Assert.Single(client.Efforts));
    }

    // ── What LM Studio is sent ─────────────────────────────────────────────────

    [Theory]
    [InlineData("Low", "low")]
    [InlineData("Medium", "medium")]
    [InlineData("High", "high")]
    public async Task LmStudio_SendsALevel_ToAModelThatPublishesIt(string effort, string sent)
    {
        using var server = UtilityCallsCarryNoReasoningTests.LmStudio("openai/gpt-oss-20b", "low", "medium", "high");
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl });

        await UtilityCallsCarryNoReasoningTests.AskAsync(client, "openai/gpt-oss-20b", Enum.Parse<ReasoningEffort>(effort));

        var body = Assert.Single(UtilityCallsCarryNoReasoningTests.ChatBodies(server));
        Assert.Equal(sent, JsonDocument.Parse(body).RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task ALevelTheServerRefuses_IsNotSentAgain_AndTheOthersStillAre()
    {
        var model = "qwen/refusing-low-" + Guid.NewGuid().ToString("N");
        bool RefusesLow(string body) => body.Contains("\"reasoning_effort\":\"low\"", StringComparison.Ordinal);
        using var server = new LoopbackHttpServer(
            (path, body) => path.StartsWith("/api/v1/models", StringComparison.Ordinal)
                                ? UtilityCallsCarryNoReasoningTests.NativeModels(model, "off", "low", "medium", "on")
                          : !path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? null
                          : RefusesLow(body) ? "{\"error\":{\"message\":\"Invalid 'reasoning_effort' value: 'low'.\"}}"
                          : UtilityCallsCarryNoReasoningTests.Sse,
            (path, body) => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) && RefusesLow(body) ? 400 : 200);
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl });

        await UtilityCallsCarryNoReasoningTests.AskAsync(client, model, ReasoningEffort.Low);   // refused, asked again
        await UtilityCallsCarryNoReasoningTests.AskAsync(client, model, ReasoningEffort.Off);   // a title: still "none"
        await UtilityCallsCarryNoReasoningTests.AskAsync(client, model, ReasoningEffort.Low);   // not sent again

        var efforts = UtilityCallsCarryNoReasoningTests.ChatBodies(server)
            .Select(b => JsonDocument.Parse(b).RootElement.TryGetProperty("reasoning_effort", out var e) ? e.GetString() : null)
            .ToList();
        Assert.Equal(["low", null, "none", null], efforts);
    }

    [Fact]
    public async Task LmStudio_SendsNothing_ForALevelTheModelDoesNotPublish()
    {
        // Publishes only "off" and "on": "low" is not a lower effort for it, it is a value its template does not know.
        using var server = UtilityCallsCarryNoReasoningTests.LmStudio("qwen/qwen3.5-9b", "off", "on");
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl });

        await UtilityCallsCarryNoReasoningTests.AskAsync(client, "qwen/qwen3.5-9b", ReasoningEffort.Low);
        await UtilityCallsCarryNoReasoningTests.AskAsync(client, "qwen/qwen3.5-9b", ReasoningEffort.Off);

        var bodies = UtilityCallsCarryNoReasoningTests.ChatBodies(server).ToList();
        Assert.Equal(2, bodies.Count);                                       // witness: both requests were read
        Assert.False(JsonDocument.Parse(bodies[0]).RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.Equal("none", JsonDocument.Parse(bodies[1]).RootElement.GetProperty("reasoning_effort").GetString());
    }
}
