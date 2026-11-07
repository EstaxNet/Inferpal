using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A plan that checks its change is not over before the check. Its check step closes on any call, so without the rule an
//  edit, then a read, read the plan finished, and the order to answer "WITHOUT calling any more tools" reaches a model
//  that ran no test. The order waits, once per edit, for a check that proved something after the last edit that landed
//  — the reading of the result bar — and the plan card says whether each check step's check ran.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class CheckStepTests
{
    private const string Plan =
        """{"goal":"fix the counter","steps":[{"i":1,"desc":"read the page","tool":"read_file"},""" +
        """{"i":2,"desc":"fix it","tool":"apply_diff"},{"i":3,"desc":"run the tests","tool":"run_tests"}]}""";
    private const string BuildThenTestsPlan =
        """{"goal":"fix the counter","steps":[{"i":1,"desc":"read the page","tool":"read_file"},""" +
        """{"i":2,"desc":"fix it","tool":"apply_diff"},{"i":3,"desc":"build","tool":"get_diagnostics"},""" +
        """{"i":4,"desc":"run the tests","tool":"run_tests"}]}""";
    private const string BaselinePlan =
        """{"goal":"fix the counter","steps":[{"i":1,"desc":"run the tests to see the failure","tool":"run_tests"},""" +
        """{"i":2,"desc":"fix it","tool":"apply_diff"}]}""";
    private const string InstallPlan =
        """{"goal":"add the package","steps":[{"i":1,"desc":"read package.json","tool":"read_file"},""" +
        """{"i":2,"desc":"add the dependency","tool":"apply_diff"},{"i":3,"desc":"npm install","tool":"run_command"}]}""";
    private const string ReadThenTestsPlan =
        """{"goal":"see whether the tests pass","steps":[{"i":1,"desc":"read the page","tool":"read_file"},""" +
        """{"i":2,"desc":"run the tests","tool":"run_tests"}]}""";
    private const string NoCheckPlan =
        """{"goal":"fix the counter","steps":[{"i":1,"desc":"read the page","tool":"read_file"},""" +
        """{"i":2,"desc":"fix it","tool":"apply_diff"}]}""";

    private const string Passed = "✓ PASSED — Failed: 0, Passed: 3, Skipped: 0, Total: 3";
    private static string Built => Strings.DiagBuildOk("Counter.csproj");
    // What run_command answers when the person refuses its card.
    private static string Refused => Strings.RunCancelled;
    private const string EditRefused = "Error: the 'apply_edits' call failed — missing path. Check the arguments against the tool's schema and try again.";

    private sealed class Scripted(IEnumerable<ChatTurnResult> script) : IChatTurnClient
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

    /// <summary>apply_diff writes, apply_edits is refused, run_tests passes, get_diagnostics builds, run_command is
    /// refused at its card, the rest reads.</summary>
    private sealed class Registry(bool offersChecks = true, params string[] editNotes) : IToolRegistry
    {
        private int _writes;
        private readonly Queue<string> _notes = new(editNotes);
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
            (offersChecks ? new[] { "read_file", "apply_diff", "apply_edits", "write_file", "run_tests", "get_diagnostics", "run_command" }
                          : new[] { "read_file", "apply_diff", "apply_edits", "write_file" })
                .Select(n => new ToolDefinition("function", new ToolFunction(n, n, new { }))).ToList();
        public int? WritesInRun => _writes;
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => Task.FromResult(name switch
        {
            "apply_diff"      => Written(),
            "write_file"      => Strings.DiffCancelled,   // refused at its card: nothing written, and no error
            "apply_edits"     => EditRefused,
            "run_tests"       => Passed,
            "get_diagnostics" => Built,
            "run_command"     => Refused,
            _                 => "@page \"/counter\"",
        });
        private string Written()
        {
            _writes++;
            return _notes.Count > 0 ? _notes.Dequeue() : "Change applied in Counter.razor.";
        }
    }

    private static int _n;
    private static ToolCallDto CallOf(string tool) => tool == "run_command"
        ? new(new ToolCallFunction(tool, JsonSerializer.SerializeToElement(new { command = $"dotnet test --no-restore -v q{Interlocked.Increment(ref _n)}" })))
        : new(new ToolCallFunction(tool, JsonSerializer.SerializeToElement(new { path = $"Counter{Interlocked.Increment(ref _n)}.razor" })));
    // Each call names another target: identical calls would be stopped by the loop detector, which is not the subject.
    private static ChatTurnResult Call(params string[] tools) => new(string.Empty, [.. tools.Select(CallOf)], 0, 0);
    private static ChatTurnResult Say(string text) => new(text, null, 0, 0);

    private static Task<(Scripted Fake, OrchestratorResult Result)> RunAsync(
        string plan, int maxIterations, params ChatTurnResult[] acts) => RunAsync(plan, maxIterations, new Registry(), null, acts);

    private static async Task<(Scripted Fake, OrchestratorResult Result)> RunAsync(
        string plan, int maxIterations, Registry tools, Action<int, AgentStepStatus>? onStepUpdate, params ChatTurnResult[] acts)
    {
        var fake = new Scripted([Say(plan), .. acts]);
        var result = await new AgentOrchestrator(fake, new InferpalConfig { ContextWindowSize = 8192, CompactionEnabled = false, AgentMaxIterations = maxIterations })
            .RunAsync(model: "m", history: [new("system", "s"), new("user", "Fix the counter.")],
                      tools: tools, onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: onStepUpdate,
                      onToolExecuted: null, onStreamReset: null, ct: CancellationToken.None);
        return (fake, result);
    }

    private static Task<(Scripted Fake, OrchestratorResult Result)> RunAsync(string plan, params ChatTurnResult[] acts) =>
        RunAsync(plan, 8, acts);

    // What the model read last before the act of index i (the plan request is Seen[0]).
    private static string LastUserBefore(Scripted fake, int act) => fake.Seen[act + 1].Last(m => m.Role == "user").Content!;
    private static bool AsksForTheAnswer(string observe) => observe.Contains("All plan steps are complete");
    private static bool AsksForTheCheck(string observe) => observe.Contains("Your plan checks the change");

    // ── What a check proved ───────────────────────────────────────────────────────

    [Fact]
    public void TheWitnesses_ReadAsTheResultBarReadsThem()
    {
        var tests = ChatTurnPolicy.ProofOf([new ToolExecution("run_tests", "{}", Passed)]);
        Assert.True(tests.TestsSinceEdit && tests.BuildSinceEdit);   // a test run builds too

        var build = ChatTurnPolicy.ProofOf([new ToolExecution("get_diagnostics", "{}", Built)]);
        Assert.True(build.BuildSinceEdit);
        Assert.False(build.TestsSinceEdit);                          // a build runs no test

        var refused = ChatTurnPolicy.ProofOf([new ToolExecution("run_command", """{"command":"dotnet test"}""", Refused)]);
        Assert.False(refused.BuildInRun);
    }

    [Fact]
    public void ATestRunThatRanNothing_ProvesNothing()
    {
        Assert.False(ChatTurnPolicy.ProofOf(
            [new ToolExecution("run_tests", """{"filter":"Nope"}""", Inferpal.Services.Tools.RunTestsTool.NoTestMatchedFilter)]).BuildInRun);
    }

    [Fact]
    public void AnEditThatFailed_AndTheMemory_AreNoChange()
    {
        var proof = ChatTurnPolicy.ProofOf([
            new ToolExecution("apply_diff", "{}", "Change applied in Counter.razor."),
            new ToolExecution("run_tests", "{}", Passed),
            new ToolExecution("apply_edits", "{}", EditRefused),
            new ToolExecution("update_memory", "{}", "Memory updated."),
        ]);

        Assert.Equal(0, proof.LastEdit);
        Assert.True(proof.TestsSinceEdit);
    }

    // ── The order to answer ───────────────────────────────────────────────────────

    [Fact]
    public async Task AReadAfterTheFix_DoesNotStandInForTheTests()
    {
        var (fake, _) = await RunAsync(Plan, Call("read_file"), Call("apply_diff"), Call("read_file"), Call("run_tests"), Say("Fixed."));

        Assert.True(AsksForTheCheck(LastUserBefore(fake, 3)));
        Assert.True(AsksForTheAnswer(LastUserBefore(fake, 4)));     // the test run after it gets the order to answer
    }

    [Fact]
    public async Task TheCheckIsAskedForOncePerEdit()
    {
        var (fake, _) = await RunAsync(Plan, Call("read_file"), Call("apply_diff"), Call("read_file"), Call("read_file"), Say("Fixed."));

        Assert.True(AsksForTheCheck(LastUserBefore(fake, 3)));
        Assert.True(AsksForTheAnswer(LastUserBefore(fake, 4)));     // not asked again: a check may not be able to run here
    }

    [Fact]
    public async Task ACommandRefusedAtItsCard_IsNoCheck()
    {
        var (fake, _) = await RunAsync(Plan, Call("read_file"), Call("apply_diff"), Call("run_command"), Say("Fixed."));

        Assert.True(AsksForTheCheck(LastUserBefore(fake, 3)));
    }

    [Fact]
    public async Task ATestRunBeforeTheFix_IsNotTheCheckAfterIt()
    {
        var (fake, _) = await RunAsync(Plan, Call("run_tests"), Call("read_file"), Call("apply_diff"), Call("read_file"), Say("Fixed."));

        Assert.True(AsksForTheCheck(LastUserBefore(fake, 4)));
    }

    [Fact]
    public async Task AStepThatRunsACommand_IsNoCheckStep()
    {
        var (fake, result) = await RunAsync(InstallPlan, Call("read_file"), Call("apply_diff"), Call("run_command"), Say("Added."));

        Assert.True(AsksForTheAnswer(LastUserBefore(fake, 3)));
        Assert.Equal(AgentStepStatus.Done, result.Plan!.Steps[2].Status);
    }

    [Fact]
    public async Task ABuild_DoesNotStandInForTheTests()
    {
        var (fake, _) = await RunAsync(BuildThenTestsPlan, Call("read_file"), Call("apply_diff"), Call("get_diagnostics"), Say("Fixed."));

        Assert.Contains("Remaining plan steps: 1", LastUserBefore(fake, 3));
    }

    [Fact]
    public async Task AReplyInProseToTheCheckPrompt_IsTheAnswer()
    {
        const string answer = "The tests cannot run here: the fix sets currentCount to 0.";
        var (fake, result) = await RunAsync(Plan, Call("read_file"), Call("apply_diff"), Call("read_file"), Say(answer));

        Assert.Equal(answer, result.FinalResponse);
        Assert.Equal(5, fake.Seen.Count);   // the plan, three calls, the answer: no nudge
        Assert.DoesNotContain(fake.Seen, request => request.Any(m => m.Content == ModelPrompts.AgentNudgeToolCall));
    }

    [Fact]
    public async Task AnEditAndATestRunInOneRound_CloseBothSteps()
    {
        var (fake, _) = await RunAsync(Plan, Call("read_file"), Call("apply_diff", "run_tests"), Say("Fixed."));

        Assert.True(AsksForTheAnswer(LastUserBefore(fake, 2)));
    }

    [Fact]
    public async Task WhereNoCheckCanRun_TheOrderToAnswerIsSent()
    {
        var (fake, _) = await RunAsync(Plan, 8, new Registry(offersChecks: false), null,
                                       Call("read_file"), Call("apply_diff"), Call("read_file"), Say("Fixed."));

        Assert.True(AsksForTheAnswer(LastUserBefore(fake, 3)));
    }

    [Fact]
    public async Task APlanWithoutACheckStep_GetsTheOrderToAnswer_AsBefore()
    {
        var (fake, _) = await RunAsync(NoCheckPlan, Call("read_file"), Call("apply_diff"), Say("Fixed."));

        Assert.True(AsksForTheAnswer(LastUserBefore(fake, 2)));
    }

    // ── What the card says ────────────────────────────────────────────────────────

    [Fact]
    public async Task AtTheEnd_TheCheckStepSaysWhetherACheckRanAfterTheLastEdit()
    {
        var (_, unchecked_) = await RunAsync(Plan, Call("read_file"), Call("apply_diff"), Call("read_file"), Say("Fixed."));
        var (_, checked_)   = await RunAsync(Plan, Call("read_file"), Call("apply_diff"), Call("run_tests"), Say("Fixed."));

        Assert.Equal(AgentStepStatus.Skipped, unchecked_.Plan!.Steps[2].Status);
        Assert.Equal(AgentStepStatus.Done, checked_.Plan!.Steps[2].Status);
    }

    [Fact]
    public async Task ACheckStepClosedByARead_SaysSkippedAtOnce()
    {
        var updates = new List<(int Step, AgentStepStatus Status)>();
        await RunAsync(Plan, 8, new Registry(), (i, s) => updates.Add((i, s)),
                       Call("read_file"), Call("apply_diff"), Call("read_file"), Say("Fixed."));

        Assert.Equal(AgentStepStatus.Skipped, updates.First(u => u.Step == 2 && u.Status != AgentStepStatus.Active).Status);
    }

    [Fact]
    public async Task ATestRunAfterTheCheckPrompt_TurnsTheStepDone()
    {
        var updates = new List<(int Step, AgentStepStatus Status)>();
        var (_, result) = await RunAsync(Plan, 8, new Registry(), (i, s) => updates.Add((i, s)),
                                         Call("read_file"), Call("apply_diff"), Call("read_file"), Call("run_tests"), Say("Fixed."));

        Assert.Contains((2, AgentStepStatus.Skipped), updates);
        Assert.Equal(AgentStepStatus.Done, result.Plan!.Steps[2].Status);
    }

    [Fact]
    public async Task AStepDone_StaysDone_AndALaterEditAsksForTheCheckAgain()
    {
        var (fake, result) = await RunAsync(Plan, Call("read_file"), Call("apply_diff"), Call("run_tests"), Call("apply_diff"), Say("Fixed."));

        Assert.True(AsksForTheCheck(LastUserBefore(fake, 4)));
        Assert.Equal(AgentStepStatus.Done, result.Plan!.Steps[2].Status);
    }

    [Fact]
    public async Task AnEditRefusedAtItsCard_IsNoChange()
    {
        // A refusal at the card does not read as a failure ("Change cancelled by user."): only the registry's count of
        // writes tells that nothing changed, so the read after it gets the order to answer, never the check prompt.
        var (fake, result) = await RunAsync(Plan, Call("read_file"), Call("apply_diff"), Call("run_tests"), Call("write_file"),
                                            Call("read_file"), Say("Fixed."));

        Assert.True(AsksForTheAnswer(LastUserBefore(fake, 5)));
        Assert.Equal(AgentStepStatus.Done, result.Plan!.Steps[2].Status);
        Assert.DoesNotContain(fake.Seen, request => request.Any(m => AsksForTheCheck(m.Content ?? string.Empty)));
    }

    [Fact]
    public async Task ARepairOfTheBuild_ComesFirst_AndDoesNotUseUpTheCheckPrompt()
    {
        var broken = "Change applied in Counter.razor.\n\n"
            + Strings.SmartFixBuildErrors(1, "Counter.razor(13,7): error RZ1006: The code block is missing a closing \"}\" character.");
        var (fake, _) = await RunAsync(Plan, 8, new Registry(true, broken), null,
                                       Call("read_file"), Call("apply_diff"), Call("read_file"), Call("get_diagnostics"), Say("Fixed."));

        Assert.Contains("no longer builds", LastUserBefore(fake, 3));
        Assert.True(AsksForTheCheck(LastUserBefore(fake, 4)));     // the build is mended, the tests still to run
    }

    [Fact]
    public async Task AtALoopStop_ACheckStepNeverReached_IsSettled()
    {
        // The same refused edit, again and again: the write step stays open, the loop detector stops the run.
        var refused = new ChatTurnResult(string.Empty,
            [new ToolCallDto(new ToolCallFunction("apply_edits", JsonSerializer.SerializeToElement(new { path = "Counter.razor" })))], 0, 0);
        var (_, result) = await RunAsync(Plan, Call("read_file"), refused, refused, refused, refused, refused);

        Assert.True(result.WasLoopDetected);
        Assert.Equal(AgentStepStatus.Skipped, result.Plan!.Steps[2].Status);
    }

    [Fact]
    public async Task ABaselineTestRunBeforeTheFix_StaysDone()
    {
        var (fake, result) = await RunAsync(BaselinePlan, Call("run_tests"), Call("apply_diff"), Say("Fixed."));

        Assert.Equal(AgentStepStatus.Done, result.Plan!.Steps[0].Status);
        Assert.True(AsksForTheAnswer(LastUserBefore(fake, 2)));     // no check step comes after the fix
    }

    [Fact]
    public async Task AtTheIterationLimit_ACheckStepLeftRunning_IsSettled()
    {
        // The refused edit keeps the tests step open: it is the running step when the limit falls.
        var (_, result) = await RunAsync(ReadThenTestsPlan, 2, Call("read_file"), Call("apply_edits"));

        Assert.True(result.ReachedIterationLimit);
        Assert.Equal(AgentStepStatus.Skipped, result.Plan!.Steps[1].Status);
    }
}
