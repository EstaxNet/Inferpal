using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A plan's edit step is done when a file was written, not when a tool was called.
//
//  On the real-condition battery (a bug three files away from its failing test) models found the
//  fix and DESCRIBED it — "Applying that makes the test pass" — without changing a file. The plan
//  was [find the test, read, read, fix, run the tests]; five reads advanced five steps, the plan read
//  complete, and the answer-now prompt told the model to answer "WITHOUT calling any more tools".
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class PlannedEditNotSkippedTests
{
    private sealed class Scripted(IEnumerable<ChatTurnResult> script) : IOllamaChatClient
    {
        private readonly Queue<ChatTurnResult> _script = new(script);
        public List<List<ChatMessageDto>> Seen { get; } = [];

        public Task<ChatTurnResult> SendChatAsync(string model, List<ChatMessageDto> messages, IToolRegistry tools,
            Action<string>? onToken, CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal,
            string? toolChoice = null, Action<string>? onThinking = null)
        {
            Seen.Add([.. messages]);
            return Task.FromResult(_script.Count > 1 ? _script.Dequeue() : _script.Peek());
        }
    }

    /// <summary>A registry that counts writes like the real one: apply_diff writes, the reads do not.</summary>
    private sealed class Registry : IToolRegistry
    {
        private int _writes;
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
            new[] { "read_file", "search_in_files", "apply_diff", "run_tests" }
                .Select(n => new ToolDefinition("function", new ToolFunction(n, n, new { }))).ToList();
        public int? WritesInRun => _writes;
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        {
            if (name == "apply_diff") _writes++;
            return Task.FromResult(name == "apply_diff" ? "OK — written" : "content");
        }
    }

    private static int _n;
    // Each call reads another file: identical calls would be stopped by the loop detector, which is not the subject.
    private static ChatTurnResult Call(string tool) =>
        new(string.Empty, [new ToolCallDto(new ToolCallFunction(tool,
            JsonSerializer.SerializeToElement(new { path = $"f{Interlocked.Increment(ref _n)}.cs" })))], 0, 0);

    private const string Plan =
        """{"goal":"fix the failing test","steps":[{"i":1,"desc":"find the test","tool":"search_in_files"},""" +
        """{"i":2,"desc":"read it","tool":"read_file"},{"i":3,"desc":"fix the bug","tool":"apply_diff"},""" +
        """{"i":4,"desc":"run the tests","tool":"run_tests"}]}""";

    private static async Task<Scripted> RunAsync(params ChatTurnResult[] acts)
    {
        var fake = new Scripted([new ChatTurnResult(Plan, null, 0, 0), .. acts, new ChatTurnResult("Done.", null, 0, 0)]);
        await new AgentOrchestrator(fake, new InferpalConfig { ContextWindowSize = 8192, CompactionEnabled = false, AgentMaxIterations = 8 })
            .RunAsync(model: "m", history: [new("system", "s"), new("user", "The test fails. Fix the bug.")],
                      tools: new Registry(), onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: null,
                      onToolExecuted: null, onStreamReset: null, ct: CancellationToken.None);
        return fake;
    }

    private static bool AnswerNowWasAsked(Scripted fake) =>
        fake.Seen.Any(seen => seen.Any(m => m.Role == "user" && m.Content!.Contains("All plan steps are complete")));

    [Fact]
    public async Task FourReads_DoNotTickOffThePlannedFix()
    {
        var fake = await RunAsync(Call("search_in_files"), Call("read_file"), Call("read_file"), Call("read_file"));

        Assert.True(fake.Seen.Count >= 6);                                   // WITNESS: the four reads ran
        Assert.False(AnswerNowWasAsked(fake));
        Assert.Contains(fake.Seen.Last(), m => m.Role == "user" && m.Content!.Contains("Remaining plan steps: 2"));
    }

    [Fact]
    public async Task AWrittenFix_TicksItOff_AndThePlanCompletes()
    {
        // Reference arm: once the fix is written and the tests run, the plan is complete as before.
        var fake = await RunAsync(Call("search_in_files"), Call("read_file"), Call("apply_diff"), Call("run_tests"));

        Assert.True(AnswerNowWasAsked(fake));
    }
}
