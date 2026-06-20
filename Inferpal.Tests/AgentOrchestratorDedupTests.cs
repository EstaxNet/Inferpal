using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Covers the intra-run tool-result cache: byte-identical repeats of IDEMPOTENT tools
// (fetch_url, web_search…) are served from cache, while read-only-but-volatile tools
// (read_file, run_tests, get_diagnostics) must re-execute — their results change after an
// intervening write, and serving a stale copy feeds the model outdated state in the very
// edit → verify cycle AgentLoopPolicy tolerates. Regression guard for the stale-cache bug.
public class AgentOrchestratorDedupTests
{
    // ── Scripted chat client: returns a queued response per call ──────────────
    private sealed class ScriptedChatClient : IOllamaChatClient
    {
        private readonly Queue<ChatTurnResult> _script;

        public ScriptedChatClient(IEnumerable<ChatTurnResult> script) => _script = new(script);

        public Task<ChatTurnResult> SendChatAsync(
            string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal, string? toolChoice = null,
            Action<string>? onThinking = null)
        {
            var reply = _script.Count > 1 ? _script.Dequeue() : _script.Peek();
            return Task.FromResult(reply);
        }
    }

    // ── Counting registry: one named tool, counts real executions ─────────────
    private sealed class CountingToolRegistry(string toolName) : IToolRegistry
    {
        public int ExecuteCount { get; private set; }

        public IReadOnlyList<ToolDefinition> Definitions { get; } =
        [
            new("function", new ToolFunction(toolName, "test tool", new { })),
        ];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        {
            ExecuteCount++;
            return Task.FromResult($"result #{ExecuteCount}");
        }
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ChatTurnResult PlanReply() =>
        new("""{"goal":"test","steps":[{"i":1,"desc":"step"}]}""", null, 0, 0);

    private static ChatTurnResult ToolCallReply(string toolName, string argsJson) =>
        new(string.Empty, [new ToolCallDto(new ToolCallFunction(toolName, Args(argsJson)))], 0, 0);

    private static Task<OrchestratorResult> RunAsync(string toolName, CountingToolRegistry registry)
    {
        // PLAN, the same tool call twice (read-only tools tolerate one repeat before loop
        // detection trips at the 3rd occurrence), then a printable final answer.
        var script = new[]
        {
            PlanReply(),
            ToolCallReply(toolName, """{"q":"same"}"""),
            ToolCallReply(toolName, """{"q":"same"}"""),
            new ChatTurnResult("done", null, 0, 0),
        };
        var orch = new AgentOrchestrator(
            new ScriptedChatClient(script),
            new InferpalConfig { ContextWindowSize = 8192, CompactionEnabled = false, AgentMaxIterations = 5 });
        return orch.RunAsync(
            model: "m",
            history: [new("system", "sys"), new("user", "go")],
            tools: registry,
            onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: null,
            onToolExecuted: null, onStreamReset: null, ct: CancellationToken.None);
    }

    [Fact]
    public async Task IdempotentTool_RepeatedCall_IsServedFromCache()
    {
        var registry = new CountingToolRegistry("fetch_url");

        var result = await RunAsync("fetch_url", registry);

        Assert.Equal(1, registry.ExecuteCount);          // second call reused the cached result
        Assert.Equal(2, result.Executions.Count);        // but both executions are reported to the UI
        Assert.Equal("done", result.FinalResponse);
    }

    [Fact]
    public async Task VolatileReadOnlyTool_RepeatedCall_ReExecutes()
    {
        // read_file is read-only for loop detection, but its result changes after an
        // intervening write — it must NEVER be served from the intra-run cache.
        var registry = new CountingToolRegistry("read_file");

        var result = await RunAsync("read_file", registry);

        Assert.Equal(2, registry.ExecuteCount);          // both calls really executed
        Assert.Equal("done", result.FinalResponse);
    }

    [Fact]
    public void MutatingAndVolatileTools_AreNotCacheable()
    {
        Assert.False(AgentOrchestrator.IsCacheable("run_command"));
        Assert.False(AgentOrchestrator.IsCacheable("write_file"));
        Assert.False(AgentOrchestrator.IsCacheable("read_file"));
        Assert.False(AgentOrchestrator.IsCacheable("run_tests"));
        Assert.False(AgentOrchestrator.IsCacheable("get_diagnostics"));

        Assert.True(AgentOrchestrator.IsCacheable("fetch_url"));
        Assert.True(AgentOrchestrator.IsCacheable("web_search"));
        Assert.True(AgentOrchestrator.IsCacheable("search_docs"));
        Assert.True(AgentOrchestrator.IsCacheable("search_codebase"));
    }
}
