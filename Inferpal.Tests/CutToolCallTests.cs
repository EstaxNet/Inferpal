using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A tool call whose reply stopped at the length limit arrives with its arguments cut mid-JSON. It is refused — right —
/// but the model was told "the output may have been cut off — resend the call with complete JSON arguments": the one
/// remedy that cannot work, since the same call is cut at the same point. The likely case is the costly one: a long
/// file written by <c>write_file</c> when the window is nearly full, refused round after round.
/// </summary>
public class CutToolCallTests
{
    private const string CutArguments = "{\"path\":\"Big.cs\",\"content\":\"public class Big\\n{\\n    // line 1";

    private static void AssertSaysTheLengthLimit(string output)
    {
        Assert.Contains("NOT executed", output);                                  // witness: the call was refused
        Assert.Contains("length limit", output);
        Assert.DoesNotContain("resend the call with complete JSON", output);
    }

    // ── The agent orchestrator ────────────────────────────────────────────────

    private sealed class ScriptedClient(params ChatTurnResult[] script) : IOllamaChatClient
    {
        private readonly Queue<ChatTurnResult> _script = new(script);

        public Task<ChatTurnResult> SendChatAsync(
            string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal, string? toolChoice = null,
            Action<string>? onThinking = null) =>
            Task.FromResult(_script.Count > 1 ? _script.Dequeue() : _script.Peek());
    }

    private sealed class WriteTool : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } = [new("function", new ToolFunction("write_file", "write", new { }))];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => Task.FromResult("written");
    }

    private static Task<OrchestratorResult> RunAsync(params ChatTurnResult[] script) =>
        new AgentOrchestrator(new ScriptedClient(script),
                              new InferpalConfig { ContextWindowSize = 8192, CompactionEnabled = false, AgentMaxIterations = 6, AgentModeEnabled = true })
            .RunAsync(model: "m", history: [new ChatMessageDto("system", "s"), new ChatMessageDto("user", "write Big.cs")],
                      tools: new WriteTool(), onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: null,
                      onToolExecuted: null, onStreamReset: null, ct: CancellationToken.None);

    private static ChatTurnResult Plan() => new("{\"goal\":\"g\",\"steps\":[{\"i\":1,\"desc\":\"write\"}]}", null, 0, 0);

    private static ChatTurnResult CutCall(bool cut) =>
        new(string.Empty, [new ToolCallDto(ToolCallArguments.Parse("write_file", CutArguments))], 0, 0, CutAtLimit: cut);

    [Fact]
    public async Task TheOrchestrator_TellsTheModelItsCallWasCutAtTheLimit()
    {
        var result = await RunAsync(Plan(), CutCall(cut: true), new ChatTurnResult("I could not write it.", null, 0, 0));

        AssertSaysTheLengthLimit(Assert.Single(result.Executions).Output);
    }

    [Fact]
    public async Task UnreadableArgumentsWithoutACut_KeepTheirMessage()
    {
        // Reference arm: a malformed call from a reply that did NOT stop at the limit can be resent whole.
        var result = await RunAsync(Plan(), CutCall(cut: false), new ChatTurnResult("done", null, 0, 0));

        var output = Assert.Single(result.Executions).Output;
        Assert.Contains("resend the call with complete JSON", output);
        Assert.DoesNotContain("length limit", output);
    }

    // ── The basic loop (chat with tools) — the production client, a real socket ─

    private static string CutCallStream() =>
        "data: " + JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { index = 0, delta = new { tool_calls = new[]
                {
                    new { index = 0, id = "c1", type = "function", function = new { name = "write_file", arguments = CutArguments } },
                } } },
            },
        }) + "\n\n"
        + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"length\"}]}\n\n"
        + "data: [DONE]\n\n";

    private static string AnswerStream() =>
        "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content = "I could not write it." } } } }) + "\n\n"
        + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
        + "data: [DONE]\n\n";

    [Fact]
    public async Task TheBasicLoop_TellsTheModelItsCallWasCutAtTheLimit()
    {
        var requests = 0;
        using var server = new LoopbackHttpServer(path =>
            !path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? null
            : Interlocked.Increment(ref requests) == 1 ? CutCallStream() : AnswerStream());
        var client = new OpenAiCompatibleClient(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl });

        var result = await client.RunAgentAsync(
            "m", [new ChatMessageDto("user", "write Big.cs")], new WriteTool(), onStep: _ => { }, onToken: null,
            CancellationToken.None);

        Assert.True(requests >= 2, $"{requests} request(s): the loop never answered the cut call");   // witness
        AssertSaysTheLengthLimit(Assert.Single(result.Executions).Output);
    }
}
