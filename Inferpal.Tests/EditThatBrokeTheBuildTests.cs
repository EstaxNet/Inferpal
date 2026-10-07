using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  An edit that was written but broke the build is not a plan step done.
//
//  Gemma 4 added a Reset button, Smart Fix answered "4 compilation error(s) detected — please fix before
//  continuing", and the orchestrator, its plan exhausted, asked for the final answer "WITHOUT calling
//  any more tools": the run ended on "I have updated the page" over a page that no longer compiled.
//  On the bench, all 19 runs that ended right after such a report failed.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class EditThatBrokeTheBuildTests
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

    private static string Broken => "Change applied in Counter.razor.\n\n" +
        Strings.SmartFixBuildErrors(4, "Counter.razor(13,7): error RZ1006: The code block is missing a closing \"}\" character.");
    private static string Clean => "Change applied in Counter.razor.\n\n" + Strings.SmartFixBuildOk;

    /// <summary>A registry whose apply_diff writes, answering with the Smart Fix notes it is given, in order.</summary>
    private sealed class Registry(params string[] editOutputs) : IToolRegistry
    {
        private readonly Queue<string> _outputs = new(editOutputs);
        private int _writes;
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
            new[] { "read_file", "write_file", "apply_diff" }.Select(n => new ToolDefinition("function", new ToolFunction(n, n, new { }))).ToList();
        public int? WritesInRun => _writes;
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        {
            if (name is not ("apply_diff" or "write_file")) return Task.FromResult("@page \"/counter\"");
            _writes++;
            return Task.FromResult(_outputs.Count > 1 ? _outputs.Dequeue() : _outputs.Peek());
        }
    }

    private static int _n;
    // Each call names another target: identical calls would be stopped by the loop detector, which is not the subject.
    private static ChatTurnResult Call(string tool) =>
        new(string.Empty, [new ToolCallDto(new ToolCallFunction(tool,
            JsonSerializer.SerializeToElement(new { path = $"Counter{Interlocked.Increment(ref _n)}.razor" })))], 0, 0);

    private const string Plan =
        """{"goal":"add a Reset button","steps":[{"i":1,"desc":"read the page","tool":"read_file"},""" +
        """{"i":2,"desc":"add the button","tool":"apply_diff"}]}""";

    // Move a type: create its file, then remove it from the old one — the build is broken in between, by construction.
    private const string MovePlan =
        """{"goal":"move Product to its own file","steps":[{"i":1,"desc":"read Catalog.cs","tool":"read_file"},""" +
        """{"i":2,"desc":"create Product.cs","tool":"write_file"},{"i":3,"desc":"remove it from Catalog.cs","tool":"apply_diff"}]}""";

    private static Task<Scripted> RunAsync(Registry tools, params ChatTurnResult[] acts) => RunAsync(Plan, tools, acts);

    private static async Task<Scripted> RunAsync(string plan, Registry tools, params ChatTurnResult[] acts)
    {
        var fake = new Scripted([new ChatTurnResult(plan, null, 0, 0), .. acts, new ChatTurnResult("Done.", null, 0, 0)]);
        await new AgentOrchestrator(fake, new InferpalConfig { ContextWindowSize = 8192, CompactionEnabled = false, AgentMaxIterations = 8 })
            .RunAsync(model: "m", history: [new("system", "s"), new("user", "Add a Reset button to the Counter page.")],
                      tools: tools, onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: null,
                      onToolExecuted: null, onStreamReset: null, ct: CancellationToken.None);
        return fake;
    }

    // The observe message the model read after the call of index i (the plan request is Seen[0]).
    private static string ObserveAfter(Scripted fake, int act) => fake.Seen[act + 1].Last(m => m.Role == "user").Content!;
    private static bool AsksToFixTheBuild(string observe) => observe.Contains("no longer builds");
    private static bool AsksForTheAnswer(string observe) => observe.Contains("All plan steps are complete");

    [Fact]
    public async Task AnEditThatBrokeTheBuild_KeepsTheRunGoing_UntilAnEditMendsIt()
    {
        var fake = await RunAsync(new Registry(Broken, Clean), Call("read_file"), Call("apply_diff"), Call("apply_diff"));

        Assert.True(AsksToFixTheBuild(ObserveAfter(fake, 2)));     // after the breaking edit: fix it, not "answer now"
        Assert.False(AsksForTheAnswer(ObserveAfter(fake, 2)));
        Assert.True(AsksForTheAnswer(ObserveAfter(fake, 3)));      // after the mending edit: the plan completes
    }

    [Fact]
    public async Task ErrorsThatStay_HoldTheRunOpenTwice_NotUntilTheIterationLimit()
    {
        var fake = await RunAsync(new Registry(Broken), Call("read_file"), Call("apply_diff"), Call("apply_diff"), Call("apply_diff"));

        Assert.Equal(2, Enumerable.Range(2, 3).Count(i => AsksToFixTheBuild(ObserveAfter(fake, i))));
        Assert.True(AsksForTheAnswer(ObserveAfter(fake, 4)));
    }

    /// <summary>
    /// ⚠ Mid-plan, a broken build is the expected state of a change across files: asked to fix it there, Devstral deleted
    /// the file it had just created (measured on hard-move-class, 6 of 8 → 3 of 8). The plan goes on as before.
    /// </summary>
    [Fact]
    public async Task ABuildBrokenMidPlan_LetsThePlanGoOn()
    {
        var fake = await RunAsync(MovePlan, new Registry(Broken, Clean), Call("read_file"), Call("write_file"), Call("apply_diff"));

        Assert.False(AsksToFixTheBuild(ObserveAfter(fake, 2)));
        Assert.Contains("Remaining plan steps: 1", ObserveAfter(fake, 2));
        Assert.True(AsksForTheAnswer(ObserveAfter(fake, 3)));
    }

    [Fact]
    public async Task AnEditThatBuilds_CompletesThePlan_AsBefore()
    {
        // Reference arm.
        var fake = await RunAsync(new Registry(Clean), Call("read_file"), Call("apply_diff"));

        Assert.True(AsksForTheAnswer(ObserveAfter(fake, 2)));
    }
}
