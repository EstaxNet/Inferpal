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
    /// <param name="countFactor">When above 0, the server counts each request at this many times its chars/4 estimate
    /// (about 2 for Japanese or Chinese text); 0, it counts nothing.</param>
    private sealed class Scripted(int? loaded, int toolTurns, InferpalConfig config, double countFactor = 0) : OpenAiCompatibleClient(config)
    {
        private int _calls;
        public List<int> ElidedPerRequest { get; } = [];

        // The turn the loop sends, effort included: the plain one forwards to it.
        public override Task<ChatTurnResult> SendChatAsync(
            string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            ReasoningEffort effort, CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal,
            string? toolChoice = null, Action<string>? onThinking = null, Action<ModelWait?>? onWaiting = null)
        {
            ElidedPerRequest.Add(messages.Count(m => m.Content?.StartsWith(Elided, StringComparison.Ordinal) == true));
            _calls++;
            var counted = countFactor > 0
                ? (int)(countFactor * (AgentOrchestrator.EstimateTokens(messages) + RequestSize.ToolTokens(tools.Definitions)))
                : 0;
            if (_calls == 1 && tools.Definitions.Count > 0 && messages.Any(m => m.Content?.Contains("\"steps\"") == true))
                return Task.FromResult(new ChatTurnResult("{\"goal\":\"g\",\"steps\":[{\"i\":1,\"desc\":\"read\"}]}", null, 0, counted));
            return Task.FromResult(_calls <= toolTurns
                ? new ChatTurnResult(string.Empty,
                    [new ToolCallDto(new ToolCallFunction("read_file", JsonDocument.Parse($"{{\"path\":\"f{_calls}.cs\"}}").RootElement.Clone()))], 0, counted)
                : new ChatTurnResult("done", null, 0, counted));
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

    /// <summary>
    /// Tool definitions of ~4,200 tokens — what the built-in tools weigh, about half of an 8,192 window — and reads of
    /// ~500 tokens: six of them stay under 80 % of the window on their own, and go past it with the definitions every
    /// request carries.
    /// </summary>
    private sealed class HeavyDefinitions : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
            [new("function", new ToolFunction("read_file", new string('d', 16_800), new { }))];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) =>
            Task.FromResult(new string('x', 2_000));
    }

    /// <summary>
    /// ⚠ The in-run check measured the messages alone, where the check between turns adds the tool definitions: a run
    /// whose results fit only without them was sent whole — refused by a server that reports its window, cut at the
    /// head by Ollama — while elision waited for a threshold the request had already passed.
    /// </summary>
    [Fact]
    public async Task TheBasicLoop_CountsTheToolDefinitionsItSends()
    {
        var client = new Scripted(loaded: null, toolTurns: 7, Config());
        await client.RunAgentAsync("m", [new ChatMessageDto("system", "s"), new ChatMessageDto("user", "read")],
                                   new HeavyDefinitions(), onStep: _ => { }, onToken: null, CancellationToken.None);

        Assert.True(client.ElidedPerRequest.Count >= 7, $"only {client.ElidedPerRequest.Count} model turn(s)");   // witness
        Assert.True(client.ElidedPerRequest.Max() > 0, "the tool definitions were left out of the run's measure");
    }

    [Fact]
    public async Task TheOrchestrator_CountsTheToolDefinitionsItSends()
    {
        var client = new Scripted(loaded: null, toolTurns: 8, Config());   // the plan, then seven reads
        await new AgentOrchestrator(client, Config()).RunAsync(
            model: "m", history: [new ChatMessageDto("system", "s"), new ChatMessageDto("user", "read")],
            tools: new HeavyDefinitions(), onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: null,
            onToolExecuted: null, onStreamReset: null, ct: CancellationToken.None);

        Assert.True(client.ElidedPerRequest.Count >= 8, $"only {client.ElidedPerRequest.Count} model turn(s)");
        Assert.True(client.ElidedPerRequest.Max() > 0, "the tool definitions were left out of the run's measure");
    }

    [Fact]
    public void LightDefinitions_ElideNothingTheMessagesDoNotNeed()
    {
        // REFERENCE ARM: the same messages with no tool definitions stay whole — the fix counts what is sent, it does
        // not lower the threshold.
        var messages = new List<ChatMessageDto> { new("system", "s"), new("user", "read") };
        for (var i = 0; i < 6; i++) messages.Add(new ChatMessageDto("tool", new string('x', 2_000)));
        messages.AddRange([new("assistant", "a"), new("user", "u"), new("assistant", "b"), new("user", "v")]);

        AgentOrchestrator.CompactRunContext(messages, anchorCount: 2, budget: 8_192, toolTokens: 0);
        Assert.DoesNotContain(messages, m => m.Content?.StartsWith(Elided, StringComparison.Ordinal) == true);

        AgentOrchestrator.CompactRunContext(messages, anchorCount: 2, budget: 8_192, toolTokens: 4_200);
        Assert.Contains(messages, m => m.Content?.StartsWith(Elided, StringComparison.Ordinal) == true);
    }

    /// <summary>
    /// ⚠ The run's estimate is chars/4, and Japanese or Chinese text weighs about twice that: a run whose estimate stays
    /// under 80 % of the window is past it for the server — cut at the head by Ollama, refused by LM Studio. The server's
    /// count of the last request scales the next measure.
    /// </summary>
    /// <remarks>Six model calls of ~1,000-token reads stay under 80 % of 8,192 by their estimate (the reference arm, a
    /// server that counts exactly the estimate, elides nothing); counted twice, they pass it by the third read.</remarks>
    [Theory]
    [InlineData(false, 2.0, true)]
    [InlineData(true, 2.0, true)]
    [InlineData(false, 1.0, false)]
    [InlineData(true, 1.0, false)]
    public async Task ARunTheServerCountsTwiceItsEstimate_IsElidedByWhatTheServerCounts(bool orchestrator, double countFactor,
                                                                                      bool elides)
    {
        var client = new Scripted(loaded: null, toolTurns: 6, Config(), countFactor);
        if (orchestrator)
            await new AgentOrchestrator(client, Config()).RunAsync(
                model: "m", history: [new ChatMessageDto("system", "s"), new ChatMessageDto("user", "read")],
                tools: new BigReads(), onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: null,
                onToolExecuted: null, onStreamReset: null, ct: CancellationToken.None);
        else
            await client.RunAgentAsync("m", [new ChatMessageDto("system", "s"), new ChatMessageDto("user", "read")],
                                       new BigReads(), onStep: _ => { }, onToken: null, CancellationToken.None);

        Assert.True(client.ElidedPerRequest.Count >= 6, $"only {client.ElidedPerRequest.Count} model turn(s)");   // witness
        Assert.Equal(elides, client.ElidedPerRequest.Max() > 0);
    }

    [Theory]
    [InlineData(2000, 1000, 2.0)]
    [InlineData(1000, 1000, 1.0)]
    [InlineData(600, 1000, 1.0)]    // a server that counts only what its cache did not serve: the estimate stands
    [InlineData(5000, 0, 1.0)]
    public void TheServerScale_NeverLowersTheEstimate(int counted, int estimated, double expected) =>
        Assert.Equal(expected, AgentOrchestrator.ServerScale(counted, estimated));

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
