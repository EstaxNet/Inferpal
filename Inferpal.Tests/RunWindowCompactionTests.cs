using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ INSIDE a run, tool results pile up and the run elides the oldest ones past 80 % of the window —
/// the CONFIGURED window. With a model LM Studio loaded smaller, an exploration that reads a few files
/// was refused mid-run ("larger than the context the model is loaded with") while the elision waited
/// for a threshold it could not reach: the between-turns check measures the loaded window, the in-run
/// one did not.
/// </summary>
public class RunWindowCompactionTests
{
    private const string Elided = "[Older tool result elided";

    /// <summary>The production client's agent loop, with its model turns and its loaded window scripted.</summary>
    private sealed class Scripted(int? loaded, int toolTurns, InferpalConfig config) : OpenAiCompatibleClient(config)
    {
        private int _calls;
        public List<int> ElidedPerRequest { get; } = [];

        public override Task<ChatTurnResult> SendChatAsync(
            string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal, string? toolChoice = null,
            Action<string>? onThinking = null)
        {
            ElidedPerRequest.Add(messages.Count(m => m.Content?.StartsWith(Elided, StringComparison.Ordinal) == true));
            _calls++;
            if (_calls == 1 && tools.Definitions.Count > 0 && messages.Any(m => m.Content?.Contains("\"steps\"") == true))
                return Task.FromResult(new ChatTurnResult("{\"goal\":\"g\",\"steps\":[{\"i\":1,\"desc\":\"read\"}]}", null, 0, 0));
            return Task.FromResult(_calls <= toolTurns
                ? new ChatTurnResult(string.Empty,
                    [new ToolCallDto(new ToolCallFunction("read_file", JsonDocument.Parse($"{{\"path\":\"f{_calls}.cs\"}}").RootElement.Clone()))], 0, 0)
                : new ChatTurnResult("done", null, 0, 0));
        }

        public override Task<int?> GetLoadedContextWindowAsync(string model, CancellationToken ct) => Task.FromResult(loaded);
    }

    /// <summary>Each read returns ~1,000 tokens: five of them pass 80 % of 4,096, not of 8,192.</summary>
    private sealed class BigReads : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } = [new("function", new ToolFunction("read_file", "read", new { }))];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) =>
            Task.FromResult(new string('x', 4_000));
    }

    private static InferpalConfig Config() => new()
    {
        Provider = "openai-compatible", BaseUrl = "http://127.0.0.1:9",
        ContextWindowSize = 8_192, CompactionEnabled = false, AgentMaxIterations = 12, AgentModeEnabled = true,
    };

    private static async Task<List<int>> BasicLoopAsync(int? loaded)
    {
        var client = new Scripted(loaded, toolTurns: 6, Config());
        await client.RunAgentAsync("m", [new ChatMessageDto("system", "s"), new ChatMessageDto("user", "read")],
                                   new BigReads(), onStep: _ => { }, onToken: null, CancellationToken.None);
        return client.ElidedPerRequest;
    }

    [Fact]
    public async Task TheBasicLoop_ElidesAgainstTheLoadedWindow()
    {
        var elided = await BasicLoopAsync(loaded: 4_096);

        Assert.True(elided.Count >= 6, $"only {elided.Count} model turn(s)");   // witness: the run went on
        Assert.True(elided.Max() > 0, "no tool result was elided before the loaded window filled up");
    }

    [Fact]
    public async Task TheBasicLoop_WithAServerThatCannotSay_KeepsTheConfiguredWindow()
    {
        // Reference arm: under 80 % of 8,192 nothing is elided.
        Assert.Equal(0, (await BasicLoopAsync(loaded: null)).Max());
    }

    [Fact]
    public async Task TheOrchestrator_ElidesAgainstTheLoadedWindow()
    {
        var client = new Scripted(loaded: 4_096, toolTurns: 6, Config());   // the plan, then five reads
        await new AgentOrchestrator(client, Config()).RunAsync(
            model: "m", history: [new ChatMessageDto("system", "s"), new ChatMessageDto("user", "read")],
            tools: new BigReads(), onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: null,
            onToolExecuted: null, onStreamReset: null, ct: CancellationToken.None);

        Assert.True(client.ElidedPerRequest.Count >= 6, $"only {client.ElidedPerRequest.Count} model turn(s)");
        Assert.True(client.ElidedPerRequest.Max() > 0, "no tool result was elided before the loaded window filled up");
    }
}
