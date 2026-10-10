using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A step the run closes at its end — on its final answer or on a repeat — is done only when it was:
//  a write step needs a write of its own. Closed done whatever happened, a plan whose edit the user
//  refused read "all done" right above "no file was changed".
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class RemainingStepsClosedTests
{
    private sealed class Scripted(IEnumerable<ChatTurnResult> script) : IOllamaChatClient
    {
        private readonly Queue<ChatTurnResult> _script = new(script);
        public Task<ChatTurnResult> SendChatAsync(string model, List<ChatMessageDto> messages, IToolRegistry tools,
            Action<string>? onToken, CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal,
            string? toolChoice = null, Action<string>? onThinking = null) =>
            Task.FromResult(_script.Count > 1 ? _script.Dequeue() : _script.Peek());
    }

    /// <summary>Counts writes like the real registry: an approved edit writes its files, a refused one writes nothing.</summary>
    private sealed class Registry(bool refuse) : IToolRegistry
    {
        private int _writes;
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
            new[] { "read_file", "apply_diff", "apply_edits" }
                .Select(n => new ToolDefinition("function", new ToolFunction(n, n, new { }))).ToList();
        public int? WritesInRun => _writes;
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        {
            if (name == "read_file") return Task.FromResult("content");
            if (refuse) return Task.FromResult("Write cancelled by the user.");
            _writes += name == "apply_edits" ? 2 : 1;
            return Task.FromResult("OK — written");
        }
    }

    private static int _n;
    private static ChatTurnResult Call(string tool, string? path = null) =>
        new(string.Empty, [new ToolCallDto(new ToolCallFunction(tool,
            JsonSerializer.SerializeToElement(new { path = path ?? $"f{Interlocked.Increment(ref _n)}.md" })))], 0, 0);

    private const string ReadThenAdd =
        """{"goal":"add a line","steps":[{"i":1,"desc":"read notes.md","tool":"read_file"},""" +
        """{"i":2,"desc":"add the line","tool":"apply_diff"}]}""";

    private const string TwoEdits =
        """{"goal":"edit two files","steps":[{"i":1,"desc":"edit a","tool":"apply_edits"},""" +
        """{"i":2,"desc":"edit b","tool":"apply_edits"}]}""";

    private static async Task<AgentStepStatus[]> RunAsync(string plan, bool refuse, params ChatTurnResult[] acts) =>
        (await RunWithResultAsync(plan, refuse, acts)).States;

    private static async Task<(AgentStepStatus[] States, OrchestratorResult Result)> RunWithResultAsync(
        string plan, bool refuse, params ChatTurnResult[] acts)
    {
        var states = new Dictionary<int, AgentStepStatus>();
        var fake = new Scripted([new ChatTurnResult(plan, null, 0, 0), .. acts, new ChatTurnResult("Done.", null, 0, 0)]);
        var result = await new AgentOrchestrator(fake, new InferpalConfig { ContextWindowSize = 8192, CompactionEnabled = false, AgentMaxIterations = 8 })
            .RunAsync(model: "m", history: [new("system", "s"), new("user", "Add the line to notes.md.")],
                      tools: new Registry(refuse), onStep: _ => { }, onToken: null, onPlanReady: null,
                      onStepUpdate: (i, s) => states[i] = s,
                      onToolExecuted: null, onStreamReset: null, ct: CancellationToken.None);
        return ([.. states.OrderBy(kv => kv.Key).Select(kv => kv.Value)], result);
    }

    [Fact]
    public async Task ARefusedEdit_LeavesItsStepSkipped_WhenTheRunAnswers()
    {
        var states = await RunAsync(ReadThenAdd, refuse: true, Call("read_file"), Call("apply_diff"));

        Assert.Equal([AgentStepStatus.Done, AgentStepStatus.Skipped], states);
    }

    [Fact]
    public async Task ARefusedEdit_LeavesItsStepSkipped_WhenTheRunStopsOnARepeat()
    {
        // The same refused edit, asked again and again: the run stops on the repeat and closes what is left.
        var (states, result) = await RunWithResultAsync(ReadThenAdd, refuse: true,
            Call("read_file"), Call("apply_diff", "notes.md"), Call("apply_diff", "notes.md"),
            Call("apply_diff", "notes.md"), Call("apply_diff", "notes.md"));

        Assert.True(result.WasLoopDetected);                             // WITNESS: the repeat ended the run
        Assert.Equal([AgentStepStatus.Done, AgentStepStatus.Skipped], states);
    }

    [Fact]
    public async Task AWrittenEdit_IsDone()
    {
        // Reference arm: the same plan, the edit approved.
        var states = await RunAsync(ReadThenAdd, refuse: false, Call("read_file"), Call("apply_diff"));

        Assert.Equal([AgentStepStatus.Done, AgentStepStatus.Done], states);
    }

    [Fact]
    public async Task OneBatchThatWritesForTwoSteps_LeavesBothDone()
    {
        // One apply_edits wrote both files: the first step claims one write, the step closed at the end the other.
        var states = await RunAsync(TwoEdits, refuse: false, Call("apply_edits"));

        Assert.Equal([AgentStepStatus.Done, AgentStepStatus.Done], states);
    }

    [Fact]
    public void ARegistryThatCountsNoWrites_ClosesEveryStepDone()
    {
        var plan = AgentPlan.TryParse(ReadThenAdd)!;
        var states = new AgentStepStatus[2];

        AgentOrchestrator.CloseRemainingSteps(plan, unclaimedWrites: null, (i, s) => states[i] = s);

        Assert.Equal([AgentStepStatus.Done, AgentStepStatus.Done], states);
    }
}
