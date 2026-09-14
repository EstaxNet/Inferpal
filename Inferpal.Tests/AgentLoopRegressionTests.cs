using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The orchestrated loop as a real turn lives it: what it leaves in the history, what it forces on
/// the model, and what it does with an error that does not come from the user.
/// </summary>
public class AgentLoopRegressionTests
{
    private sealed class RecordingClient(IEnumerable<ChatTurnResult> script) : IOllamaChatClient
    {
        private readonly Queue<ChatTurnResult> _script = new(script);
        public List<string?> ToolChoices { get; } = [];

        public Task<ChatTurnResult> SendChatAsync(
            string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal, string? toolChoice = null,
            Action<string>? onThinking = null)
        {
            ToolChoices.Add(toolChoice);
            return Task.FromResult(_script.Count > 1 ? _script.Dequeue() : _script.Peek());
        }
    }

    private sealed class StubRegistry(string toolName, Func<Task<string>>? run = null) : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
            [new("function", new ToolFunction(toolName, "test tool", new { }))];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) =>
            run?.Invoke() ?? Task.FromResult("result");
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ChatTurnResult Plan(int steps) => new(
        "{\"goal\":\"g\",\"steps\":[" + string.Join(",", Enumerable.Range(1, steps).Select(i => $"{{\"i\":{i},\"desc\":\"s{i}\"}}")) + "]}",
        null, 0, 0);

    private static ChatTurnResult Call(string tool, string args) =>
        new(string.Empty, [new ToolCallDto(new ToolCallFunction(tool, Args(args)))], 0, 0);

    private static ChatTurnResult Text(string text) => new(text, null, 0, 0);

    private static InferpalConfig Config() =>
        new() { ContextWindowSize = 8192, CompactionEnabled = false, AgentMaxIterations = 6, AgentModeEnabled = true };

    private static Task<OrchestratorResult> RunAsync(IOllamaChatClient client, IToolRegistry tools, List<ChatMessageDto> history) =>
        new AgentOrchestrator(client, Config()).RunAsync(
            model: "m", history: history, tools: tools,
            onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: null,
            onToolExecuted: null, onStreamReset: null, ct: CancellationToken.None);

    // ── A tool's own timeout is not the user pressing Stop ─────────────────────

    /// <summary>
    /// <c>fetch_url</c> on a slow site exceeds its <c>HttpClient</c>'s <c>Timeout</c>: that is a
    /// <c>TaskCanceledException</c>, and the WHOLE run stopped as if on Stop — results lost, the
    /// background task marked "cancelled". It is an error of the tool, for the model.
    /// </summary>
    [Fact]
    public async Task AToolsOwnTimeout_IsAnErrorForTheModel_NotAStopOfTheRun()
    {
        var tools = new StubRegistry("fetch_url",
            () => Task.FromException<string>(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing.")));

        var result = await AgentOrchestrator.ExecuteToolSafeAsync(tools, "fetch_url", Args("{}"), CancellationToken.None);

        Assert.Contains("fetch_url", result, StringComparison.Ordinal);
    }

    /// <summary>Witness: the user's Stop still stops the run.</summary>
    [Fact]
    public async Task TheUsersCancellation_StillStopsTheRun()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var tools = new StubRegistry("fetch_url",
            () => Task.FromException<string>(new OperationCanceledException(cts.Token)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AgentOrchestrator.ExecuteToolSafeAsync(tools, "fetch_url", Args("{}"), cts.Token));
    }

    // ── A plan the model got half right ────────────────────────────────────────

    /// <summary>
    /// <c>{"steps":[null]}</c> is valid JSON: it passed the "at least one step" check, and the first iteration
    /// dereferenced the null step — an exception the run does not catch, where an unreadable plan falls back.
    /// </summary>
    [Fact]
    public async Task APlanWithANullStep_FallsBackInsteadOfFailingTheRun()
    {
        var client = new RecordingClient(
        [
            new ChatTurnResult("{\"goal\":\"g\",\"steps\":[null]}", null, 0, 0),
            Text("the answer"),
        ]);

        var result = await RunAsync(client, new StubRegistry("read_file"),
                                    [new("system", "sys"), new("user", "question")]);

        Assert.Contains("the answer", result.FinalResponse, StringComparison.Ordinal);
        Assert.NotNull(result.Plan);
        Assert.All(result.Plan!.Steps, step => Assert.NotNull(step));
    }

    [Fact]
    public void ANullStepAmongRealOnes_IsDropped_AndAWellFormedPlanIsUntouched()
    {
        // Witness: a well-formed plan keeps every step.
        Assert.Equal(2, AgentPlan.TryParse(Plan(2).TextContent)!.Steps.Count);

        var mixed = AgentPlan.TryParse("{\"goal\":null,\"steps\":[{\"i\":1,\"desc\":\"a\"},null,{\"i\":2,\"desc\":null}]}");

        Assert.NotNull(mixed);
        Assert.Equal(2, mixed!.Steps.Count);
        Assert.All(mixed.Steps, step => Assert.NotNull(step.Description));
        Assert.NotNull(mixed.Goal);
    }

    // ── tool_choice ────────────────────────────────────────────────────────────

    /// <summary>
    /// A single empty turn at the start triggered a forced retry — and the retry counter never went
    /// back down: EVERY following ACT went out with <c>tool_choice: "required"</c>, and the model
    /// could no longer answer in prose.
    /// </summary>
    [Fact]
    public async Task AStallRetry_ForcesTheNextCallOnly_NotTheRestOfTheRun()
    {
        var client = new RecordingClient(
        [
            Plan(2),
            Text(""),                                   // stall → retry, forced
            Call("web_search", """{"q":"x"}"""),        // the forced retry calls a tool
            Text("the answer"),
        ]);

        await RunAsync(client, new StubRegistry("web_search"),
                       [new("system", "sys"), new("user", "question")]);

        // plan, first ACT, forced retry, then the ACT after the tool ran.
        Assert.Equal("required", client.ToolChoices[1]);
        Assert.Equal("required", client.ToolChoices[2]);
        Assert.Null(client.ToolChoices[3]);
    }

    // ── What the run leaves in the history ────────────────────────────────────

    /// <summary>
    /// On a detected loop, the assistant message that requested the repeated call stayed in the
    /// history WITHOUT an answer: the host reuses it as is, and every OpenAI-compatible server then
    /// refuses each request until /clear.
    /// </summary>
    [Fact]
    public async Task ALoopDetectedRun_LeavesNoUnansweredCallInTheHistory()
    {
        var client = new RecordingClient(
        [
            Plan(1),
            Call("write_file", """{"path":"a.cs","content":"x"}"""),
            Call("write_file", """{"path":"a.cs","content":"x"}"""),   // verbatim repeat → loop
            Text("done"),                                              // synthesis
        ]);

        var result = await RunAsync(client, new StubRegistry("write_file"),
                                    [new("system", "sys"), new("user", "question")]);

        Assert.True(result.WasLoopDetected);
        var next = result.UpdatedHistory.Append(new ChatMessageDto("user", "next question")).ToList();
        Assert.False(ToolBlockBoundary.HasOrphanedToolMessage(next));
    }

    /// <summary>
    /// The synthesized answer is the one the user read: it must be the one the model reads back on
    /// the next turn, not the empty turn it replaced.
    /// </summary>
    [Fact]
    public async Task ASynthesizedAnswer_IsTheOneKeptInTheHistory()
    {
        var client = new RecordingClient(
        [
            Plan(1),
            Call("web_search", """{"q":"x"}"""),
            Text(""),                   // no printable answer after tools ran → synthesis
            Text("synthesized answer"),
        ]);

        var result = await RunAsync(client, new StubRegistry("web_search"),
                                    [new("system", "sys"), new("user", "question")]);

        Assert.Equal("synthesized answer", result.FinalResponse);
        var last = result.UpdatedHistory.Last();
        Assert.Equal("assistant", last.Role);
        Assert.Equal("synthesized answer", last.Content);
    }

    /// <summary>
    /// The host keeps the whole orchestrated history, scaffolding included (plan prompt, execution
    /// order, one observation prompt per iteration). Counting those as user turns dropped the REAL
    /// previous question although the user had asked to keep two.
    /// </summary>
    [Fact]
    public async Task KeepTurns_CountsTheUsersQuestions_NotTheLoopsScaffolding()
    {
        List<ChatMessageDto> history = [new("system", "sys"), new("user", "Q1")];
        var run1 = await RunAsync(
            new RecordingClient([Plan(1), Call("web_search", """{"q":"1"}"""), Text("answer 1")]),
            new StubRegistry("web_search"), history);

        var second = run1.UpdatedHistory.Append(new ChatMessageDto("user", "Q2")).ToList();
        var run2 = await RunAsync(
            new RecordingClient([Plan(1), Call("web_search", """{"q":"2"}"""), Text("answer 2")]),
            new StubRegistry("web_search"), second);

        var plan = HistoryCompaction.Decide(
            run2.UpdatedHistory, contextWindowSize: 1000, lastPromptTokens: 900,
            keepTurnsConfig: 2, kvAnchorMessages: 0, compactionEnabled: false);

        Assert.DoesNotContain(HistoryCompaction.SliceToCompact(run2.UpdatedHistory, plan),
                              m => m.Content == "Q1");
    }

    // ── Intra-run summary ─────────────────────────────────────────────────────

    /// <summary>
    /// A first overflow without enough old turns to summarize "burned" the run's only summary: the
    /// following overflows, which could be summarized, were only elided.
    /// </summary>
    [Fact]
    public async Task TooLittleToSummarize_DoesNotSpendTheRunsOnlySummary()
    {
        var client = new RecordingClient([Text("SUMMARY")]);
        var orch   = new AgentOrchestrator(client, new InferpalConfig { ContextWindowSize = 8192, CompactionEnabled = true });
        var msgs   = new List<ChatMessageDto> { new("system", new string('s', 4000)), new("user", "go") }
            .Concat(Enumerable.Range(0, 6).Select(_ => new ChatMessageDto("tool", new string('x', 8000))))
            .ToList();

        var spent = await orch.CompactRunContextAsync(msgs, anchorCount: 2, model: "m",
            alreadySummarized: false, onStep: _ => { }, ct: CancellationToken.None);

        Assert.Empty(client.ToolChoices);   // no summary call: a single old message is not worth one
        Assert.False(spent);
    }
}
