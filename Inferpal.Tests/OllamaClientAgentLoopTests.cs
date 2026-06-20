using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Covers the basic agent loop (OllamaClient.RunAgentAsync) now that it is a thin loop over
// the overridable SendChatAsync: shared context protections (CapForContext, elision), loop
// detection, the iteration cap, and HTTP-error propagation — all without any network.
public class OllamaClientAgentLoopTests
{
    // ── OllamaClient with a scripted SendChatAsync (one entry per model turn) ──
    private sealed class ScriptedOllamaClient : OllamaClient
    {
        private readonly Queue<Func<ChatTurnResult>> _script;
        public List<List<ChatMessageDto>> SeenMessages { get; } = [];

        public ScriptedOllamaClient(InferpalConfig config, IEnumerable<Func<ChatTurnResult>> script)
            : base(config) => _script = new(script);

        public override Task<ChatTurnResult> SendChatAsync(
            string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal, string? toolChoice = null,
            Action<string>? onThinking = null)
        {
            SeenMessages.Add(new List<ChatMessageDto>(messages));
            var reply = _script.Count > 1 ? _script.Dequeue() : _script.Peek();
            return Task.FromResult(reply());
        }
    }

    private sealed class StubToolRegistry(string toolName, Func<string> result) : IToolRegistry
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
            return Task.FromResult(result());
        }
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Func<ChatTurnResult> ToolCallReply(string toolName, string argsJson) =>
        () => new(string.Empty, [new ToolCallDto(new ToolCallFunction(toolName, Args(argsJson)))], 0, 0);

    private static Func<ChatTurnResult> TextReply(string text) => () => new(text, null, 0, 0);

    private static InferpalConfig Config(int maxIterations = 5, int contextWindow = 8192) =>
        new() { AgentMaxIterations = maxIterations, ContextWindowSize = contextWindow };

    private static Task<AgentResult> RunAsync(ScriptedOllamaClient client, IToolRegistry tools) =>
        client.RunAgentAsync(
            model: "m",
            history: [new("system", "sys"), new("user", "go")],
            tools: tools,
            onStep: _ => { }, onToken: null, ct: CancellationToken.None);

    [Fact]
    public async Task ToolResult_IsCappedInContext_ButFullInExecution()
    {
        // A huge tool output must be size-capped in the model context (it would blow num_ctx
        // and make Ollama silently truncate the head) while the UI execution keeps it whole.
        var huge     = new string('x', AgentOrchestrator.MaxToolResultCharsInContext + 5_000);
        var registry = new StubToolRegistry("read_file", () => huge);
        var client   = new ScriptedOllamaClient(Config(),
            [ToolCallReply("read_file", """{"path":"big.cs"}"""), TextReply("done")]);

        var result = await RunAsync(client, registry);

        Assert.Equal("done", result.FinalResponse);
        Assert.Equal(huge, result.Executions.Single().Output);   // UI gets the full output

        var toolMsg = result.UpdatedHistory.Single(m => m.Role == "tool");
        Assert.True(toolMsg.Content!.Length < huge.Length);      // context copy is capped
        Assert.Contains("truncated", toolMsg.Content);
    }

    [Fact]
    public async Task IterationCap_ReturnsIterationLimit()
    {
        // The model keeps calling tools (distinct args, so loop detection stays quiet):
        // the loop must stop at AgentMaxIterations and report the limit.
        var registry = new StubToolRegistry("read_file", () => "ok");
        var client   = new ScriptedOllamaClient(Config(maxIterations: 3),
        [
            ToolCallReply("read_file", """{"path":"a.cs"}"""),
            ToolCallReply("read_file", """{"path":"b.cs"}"""),
            ToolCallReply("read_file", """{"path":"c.cs"}"""),
            ToolCallReply("read_file", """{"path":"d.cs"}"""),
        ]);

        var result = await RunAsync(client, registry);

        Assert.Equal(Strings.MsgIterationLimit, result.FinalResponse);
        Assert.Equal(3, result.Executions.Count);
    }

    [Fact]
    public async Task RepeatedMutatingBatch_AbortsAsLoop()
    {
        // A verbatim repeat of a mutating call is a stall: abort on the 2nd occurrence,
        // with an empty response (work was done) so the UI shows its "✓ Done" summary.
        var registry = new StubToolRegistry("write_file", () => "written");
        var client   = new ScriptedOllamaClient(Config(),
        [
            ToolCallReply("write_file", """{"path":"a.cs","content":"x"}"""),
            ToolCallReply("write_file", """{"path":"a.cs","content":"x"}"""),
        ]);

        var result = await RunAsync(client, registry);

        Assert.Equal(string.Empty, result.FinalResponse);
        Assert.Equal(1, registry.ExecuteCount);   // the repeat was never executed
    }

    [Fact]
    public async Task HttpError_ReturnsErrorMessage_InsteadOfThrowing()
    {
        var registry = new StubToolRegistry("read_file", () => "ok");
        var client   = new ScriptedOllamaClient(Config(),
            [() => throw new AgentHttpException("boom", isTimeout: false)]);

        var result = await RunAsync(client, registry);

        Assert.Equal("boom", result.FinalResponse);
        Assert.Empty(result.Executions);
    }

    [Fact]
    public async Task OverflowingRun_ElidesOldestToolResults_KeepsAnchoredHead()
    {
        // Tiny num_ctx + repeated large tool results: by the later turns the oldest tool
        // results must have been replaced by the elision placeholder while the system
        // prompt and the user's task survive at the head. (Elision spares the most recent
        // 5 messages, so the history must grow past ~9 entries before it can bite.)
        var big      = new string('y', 4_000);                    // ~1000 tokens each
        var registry = new StubToolRegistry("read_file", () => big);
        var client   = new ScriptedOllamaClient(Config(maxIterations: 6, contextWindow: 2_000),
        [
            ToolCallReply("read_file", """{"path":"a.cs"}"""),
            ToolCallReply("read_file", """{"path":"b.cs"}"""),
            ToolCallReply("read_file", """{"path":"c.cs"}"""),
            ToolCallReply("read_file", """{"path":"d.cs"}"""),
            TextReply("done"),
        ]);

        var result = await RunAsync(client, registry);

        Assert.Equal("done", result.FinalResponse);
        // The head never moves: elision only touches tool results above the anchor.
        var lastSent = client.SeenMessages.Last();
        Assert.Equal("sys", lastSent[0].Content);
        Assert.Equal("go",  lastSent[1].Content);
        // At least one earlier tool result was elided to stay under the budget.
        Assert.Contains(lastSent, m => m.Role == "tool" && m.Content!.Contains("elided"));
    }

    // ── Consecutive same-role coalescing (shared with the OpenAI-compat path) ──
    // The Ollama client now normalizes the history too: defence-in-depth for any model whose
    // template is strict, plus a cleaner prefix for KV-cache reuse. The transform is wire-agnostic,
    // so the Ollama path relies on the same shared helper rather than passing the raw history.

    [Fact]
    public void CoalesceConsecutiveRoles_MergesUserTurns_FoldsObserveNudgeIntoToolResult()
    {
        var args = Args("""{"path":"a.cs"}""");
        var merged = InferenceProviderBase.CoalesceConsecutiveRoles(
        [
            new("system", "sys"),
            new("user", "context"),
            new("user", "question"),                                     // two users → one
            new("assistant", null, [new ToolCallDto(new ToolCallFunction("read_file", args))]),
            new("tool", "body"),
            new("user", "observe"),                                      // user after tool → folded into the tool result
        ]);

        Assert.Equal(4, merged.Count);
        Assert.Equal("user", merged[1].Role);
        Assert.Equal("context\n\nquestion", merged[1].Content);
        Assert.Equal("assistant", merged[2].Role);
        Assert.NotNull(merged[2].ToolCalls);                            // tool_calls turn untouched
        Assert.Equal("tool", merged[3].Role);
        Assert.Equal("body\n\nobserve", merged[3].Content);            // OBSERVER nudge folded into the tool result
    }
}
