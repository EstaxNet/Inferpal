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
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

// /tdd: the fix-until-green loop — verdict parsing across
// runners, run_tests argument plumbing (path + filter), the agent hand-off per red round, the
// iteration budget and the no-runner early exit.
public class TddTests
{
    /// <summary>Registry scripted with a queue of run_tests outputs; records every call.</summary>
    private sealed class FakeToolRegistry : IToolRegistry
    {
        public Queue<string> TestOutputs { get; } = new();
        public List<(string Name, string Args)> Calls { get; } = [];
        public IReadOnlyList<ToolDefinition> Definitions => [];
        public DiffInfo? ConsumeDiff() => null;

        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        {
            Calls.Add((name, args.GetRawText()));
            return Task.FromResult(TestOutputs.Count > 0 ? TestOutputs.Dequeue() : "✓ PASSED");
        }
    }

    private const string Red   = "✗ FAILED — Failed: 1, Passed: 4, Skipped: 0, Total: 5\n\nFailing tests:\n  ✗ MyTest";
    private const string Green = "✓ PASSED — Failed: 0, Passed: 5, Skipped: 0, Total: 5";

    private static Task<TddCommandHandler.TddCommandResult> RunAsync(
        FakeInferenceProvider client, FakeToolRegistry tools, string[] parts,
        Action<string, bool>? onTestReport = null, string? systemPrompt = null,
        Services.Debugging.ITestDebugCapture? capture = null, IApprovalService? approval = null,
        Action<string>? onProgress = null) =>
        TddCommandHandler.HandleAsync(
            client, new InferpalConfig { DefaultModel = "big", AgentModel = "coder" },
            tools, systemPrompt, parts, projectRoot: @"C:\proj",
            onProgress, onTestReport, onStep: null, onToken: null, onFixResult: null,
            CancellationToken.None, capture, approval);

    // ── §25 fakes: capture port + approval, both recording ─────────────────────────
    private sealed class FakeCapture : Services.Debugging.ITestDebugCapture
    {
        public bool IsAvailable { get; set; } = true;
        public Services.Debugging.DebugStopState? State { get; set; }
        public List<string> Captured { get; } = [];
        public Task<Services.Debugging.DebugStopState?> CaptureAsync(string fqn, string root, CancellationToken ct)
        { Captured.Add(fqn); return Task.FromResult(State); }
    }

    private sealed class RecordingApproval(bool answer) : IApprovalService
    {
        public List<(string Tool, string? Subject, bool Forced)> Asked { get; } = [];
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
            string? subject = null, DiffInfo? diff = null, bool forcePrompt = false)
        { Asked.Add((toolName, subject, forcePrompt)); return Task.FromResult(answer); }
    }

    private const string RedFqn =
        "✗ FAILED — Failed: 1, Passed: 4, Skipped: 0, Total: 5\n\nFailing tests:\n  ✗ My.Tests.Class.Method";

    [Fact]
    public async Task GreenFirstRound_StopsWithoutAgent()
    {
        var client = new FakeInferenceProvider();
        var tools  = new FakeToolRegistry();
        tools.TestOutputs.Enqueue(Green);

        var result = await RunAsync(client, tools, ["/tdd"]);

        Assert.Equal(Strings.TddSuccess(1), result.Message);
        Assert.Empty(client.AgentRuns);
        var call = Assert.Single(tools.Calls);
        Assert.Equal("run_tests", call.Name);
        Assert.Contains(@"C:\\proj", call.Args);          // path plumbed (JSON-escaped)
        Assert.DoesNotContain("filter", call.Args);       // no filter given → none sent
    }

    [Fact]
    public async Task RedThenGreen_RunsOneFixWithAgentModelAndReport()
    {
        var client = new FakeInferenceProvider();
        var tools  = new FakeToolRegistry();
        tools.TestOutputs.Enqueue(Red);
        tools.TestOutputs.Enqueue(Green);
        var reports = new List<(string Output, bool Green)>();

        var result = await RunAsync(client, tools, ["/tdd", "MyTest"],
            onTestReport: (o, g) => reports.Add((o, g)), systemPrompt: "SYS");

        Assert.Equal(Strings.TddSuccess(2), result.Message);

        var run = Assert.Single(client.AgentRuns);
        Assert.Equal("coder", run.Model);                             // agent role, not chat model
        Assert.Equal("system", run.History[0].Role);
        Assert.Equal("SYS", run.History[0].Content);
        Assert.Contains("MyTest", run.History[^1].Content);           // report + filter in the prompt
        Assert.Contains("✗ FAILED", run.History[^1].Content);

        Assert.Equal(2, tools.Calls.Count(c => c.Name == "run_tests"));
        Assert.All(tools.Calls, c => Assert.Contains("\"filter\":\"MyTest\"", c.Args));
        Assert.Equal(2, reports.Count);
        Assert.Equal((Red, false),   reports[0]);
        Assert.Equal((Green, true),  reports[1]);
    }

    [Fact]
    public async Task AlwaysRed_GivesUpAfterBudget()
    {
        var client = new FakeInferenceProvider();
        var tools  = new FakeToolRegistry();
        for (int i = 0; i < TddCommandHandler.MaxRounds; i++) tools.TestOutputs.Enqueue(Red);

        var result = await RunAsync(client, tools, ["/tdd"]);

        Assert.Equal(Strings.TddGiveUp(TddCommandHandler.MaxRounds), result.Message);
        Assert.Equal(TddCommandHandler.MaxRounds, tools.Calls.Count);          // 5 test runs
        Assert.Equal(TddCommandHandler.MaxRounds - 1, client.AgentRuns.Count); // 4 fix rounds
    }

    /// <summary>
    /// A run killed at its budget ends the loop — the fourth state, and the only one that can
    /// arrive wearing a green summary.
    /// </summary>
    /// <remarks>
    /// ⚠ On a solution whose first project finishes and whose second hangs, the killed run still
    /// carries the first one's pass: read as a verdict, the loop announced success on a run where
    /// half the suite never started. Read as a red one, it would be five rounds of patches against
    /// tests that never failed. Neither — it stops, and names the budget.
    /// </remarks>
    [Fact]
    public async Task ARunStoppedAtItsBudget_EndsTheLoop_InsteadOfBeingReadAsAVerdict()
    {
        var client = new FakeInferenceProvider();
        var tools  = new FakeToolRegistry();
        tools.TestOutputs.Enqueue(Services.Tools.RunTestsTool.StoppedAtBudgetLine(120) + "\n\n" + Green);

        var result = await RunAsync(client, tools, ["/tdd"]);

        Assert.StartsWith(Strings.TddStoppedAtBudget, result.Message);
        Assert.Empty(client.AgentRuns);   // no speculative fix round
        Assert.Single(tools.Calls);       // and no second run
    }

    [Fact]
    public async Task NoRunnerDetected_SurfacesToolMessage_WithoutLooping()
    {
        var client = new FakeInferenceProvider();
        var tools  = new FakeToolRegistry();
        tools.TestOutputs.Enqueue("No test runner detected. Provide 'path' to a project, or set 'runner' explicitly (dotnet / pytest / npm / cargo / go).");

        var result = await RunAsync(client, tools, ["/tdd"]);

        Assert.StartsWith("No test runner detected", result.Message);
        Assert.Empty(client.AgentRuns);
        Assert.Single(tools.Calls);
    }

    [Theory]
    [InlineData("✓ PASSED — Failed: 0, Passed: 5, Skipped: 0, Total: 5", true)]   // dotnet/cargo
    [InlineData("✓ Tests passed.", true)]                                          // go
    [InlineData("✗ FAILED — Failed: 1, Passed: 4, Skipped: 0, Total: 5", false)]
    [InlineData("5 passed in 0.21s", true)]                                        // pytest green
    [InlineData("1 failed, 4 passed in 0.30s", false)]                             // pytest red
    [InlineData("2 errors, 3 passed in 0.30s", false)]                             // pytest errors
    [InlineData("npm ERR! test script exited with code 1", false)]                 // raw dump → red
    public void TestsPassed_ReadsEveryRunnerVerdict(string output, bool expected)
    {
        Assert.Equal(expected, TddCommandHandler.TestsPassed(output));
    }

    [Fact]
    public void BuildFixPrompt_EmbedsReportAndFilter()
    {
        var prompt = TddCommandHandler.BuildFixPrompt(Red, "MyTest");
        Assert.Contains("✗ FAILED", prompt);
        Assert.Contains("'MyTest'", prompt);
        Assert.Contains("apply_diff", prompt);

        Assert.Contains("the failing tests pass",
            TddCommandHandler.BuildFixPrompt(Green, null).Replace('\n', ' '));
    }

    // ── §25: debugger state at the failure point ────────────────────────────────────

    private static Services.Debugging.DebugStopState SomeState() => new(
        "exception", 1,
        [new Services.Debugging.DebugFrame(1, "My.Tests.Class.Method", @"C:\proj\A.cs", 12)],
        [new Services.Debugging.DebugVariable("line", "string", "\"REBOOT\"")],
        "IndexOutOfRangeException");

    [Fact]
    public void BuildFixPrompt_InsertsTheDebuggerBlockBeforeTheRules()
    {
        // Measured placement (probe 2026-08-20): appended after the rules, the model narrated
        // its diagnosis without ever writing — both prompt variants must end on the same rules.
        var block  = "## Debugger paused\nStop reason: exception";
        var prompt = TddCommandHandler.BuildFixPrompt(Red, null, block);

        var blockAt = prompt.IndexOf("## Debugger paused", StringComparison.Ordinal);
        var rulesAt = prompt.IndexOf("Rules:", StringComparison.Ordinal);
        Assert.True(blockAt > 0 && rulesAt > blockAt, "the block must sit before the Rules section");
        Assert.EndsWith("instead of guessing.", prompt.TrimEnd());
    }

    [Fact]
    public void FirstFailingTest_ReadsTheFqn_AndIgnoresSummaryAndNonFqnLines()
    {
        Assert.Equal("My.Tests.Class.Method", TddCommandHandler.FirstFailingTest(RedFqn));
        Assert.Null(TddCommandHandler.FirstFailingTest(Red));    // "MyTest" is not an FQN
        Assert.Null(TddCommandHandler.FirstFailingTest(Green));
    }

    [Fact]
    public async Task Capture_InjectsTheStateBlock_AndAsksApprovalOncePerRun()
    {
        var client = new FakeInferenceProvider();
        var tools  = new FakeToolRegistry();
        tools.TestOutputs.Enqueue(RedFqn);
        tools.TestOutputs.Enqueue(RedFqn);
        tools.TestOutputs.Enqueue(Green);
        var capture  = new FakeCapture { State = SomeState() };
        var approval = new RecordingApproval(answer: true);

        await RunAsync(client, tools, ["/tdd"], capture: capture, approval: approval);

        Assert.Equal(2, capture.Captured.Count);                 // one capture per red round
        Assert.All(capture.Captured, fqn => Assert.Equal("My.Tests.Class.Method", fqn));
        var ask = Assert.Single(approval.Asked);                 // consent granularity = the run
        Assert.Equal(("debug_test", "My.Tests.Class.Method", false), ask);
        var prompt = client.AgentRuns[0].History[^1].Content;
        Assert.Contains("## Debugger paused", prompt);
        Assert.Contains("\"REBOOT\"", prompt);
        Assert.True(prompt.IndexOf("## Debugger paused", StringComparison.Ordinal)
                    < prompt.IndexOf("Rules:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CaptureFailure_SaysSo_AndTheRoundRunsPlain()
    {
        var client = new FakeInferenceProvider();
        var tools  = new FakeToolRegistry();
        tools.TestOutputs.Enqueue(RedFqn);
        tools.TestOutputs.Enqueue(Green);
        var capture  = new FakeCapture { State = null };         // capture fails
        var progress = new List<string>();

        await RunAsync(client, tools, ["/tdd"], capture: capture, approval: new RecordingApproval(true),
                       onProgress: progress.Add);

        Assert.Single(capture.Captured);
        Assert.Contains(Strings.TddDebugCaptureFailed, progress);   // no silent fallback
        Assert.DoesNotContain("## Debugger paused", client.AgentRuns[0].History[^1].Content);
    }

    [Fact]
    public async Task ApprovalDeclined_DisablesTheCaptureForTheRestOfTheRun()
    {
        var client = new FakeInferenceProvider();
        var tools  = new FakeToolRegistry();
        tools.TestOutputs.Enqueue(RedFqn);
        tools.TestOutputs.Enqueue(RedFqn);
        tools.TestOutputs.Enqueue(Green);
        var capture  = new FakeCapture { State = SomeState() };
        var approval = new RecordingApproval(answer: false);

        await RunAsync(client, tools, ["/tdd"], capture: capture, approval: approval);

        Assert.Single(approval.Asked);                            // asked once, not per round
        Assert.Empty(capture.Captured);                           // never captured
        Assert.DoesNotContain("## Debugger paused", client.AgentRuns[0].History[^1].Content);
    }

    [Fact]
    public async Task NoCapturePort_TheLoopIsUnchanged()
    {
        var client = new FakeInferenceProvider();
        var tools  = new FakeToolRegistry();
        tools.TestOutputs.Enqueue(RedFqn);
        tools.TestOutputs.Enqueue(Green);
        var progress = new List<string>();

        await RunAsync(client, tools, ["/tdd"], onProgress: progress.Add);   // no port, no approval

        Assert.DoesNotContain("Debugger", client.AgentRuns[0].History[^1].Content);
        // An ABSENT port is not a broken port: on the VS Code side without the §21 `debug`
        // capability, §25 does not exist, and announcing a missing capability there would be
        // noise on every /tdd.
        Assert.DoesNotContain(Strings.TddDebugCaptureUnavailable, progress);
    }

    [Fact]
    public async Task CaptureUnavailable_SaysSoOnce_AndTheLoopRunsPlain()
    {
        // The measured defect, in one case. The in-process debugger driver had not started, so
        // `IsAvailable` was false: /tdd ran its bare red loop WITHOUT a word, nobody saw that the
        // §25 capture had never been offered, and the probe reading it scored that as a product
        // failure - where there was only a missing capability.
        var client = new FakeInferenceProvider();
        var tools  = new FakeToolRegistry();
        tools.TestOutputs.Enqueue(RedFqn);
        tools.TestOutputs.Enqueue(RedFqn);
        tools.TestOutputs.Enqueue(Green);
        var capture  = new FakeCapture { IsAvailable = false, State = SomeState() };
        var approval = new RecordingApproval(true);
        var progress = new List<string>();

        await RunAsync(client, tools, ["/tdd"], capture: capture, approval: approval,
                       onProgress: progress.Add);

        // Said once - not per round: two red rounds are not two failures.
        Assert.Single(progress, p => p == Strings.TddDebugCaptureUnavailable);
        Assert.Empty(capture.Captured);      // nothing was captured...
        Assert.Empty(approval.Asked);        // ...and nothing was asked for it
        Assert.DoesNotContain("## Debugger paused", client.AgentRuns[0].History[^1].Content);
    }

    [Fact]
    public async Task ApprovalDeclined_IsNotReportedAsAnOutage()
    {
        // A refusal is a user decision, which they already read on their card. Conflating it with
        // the failure above would send people hunting a dead driver after every "no".
        var client = new FakeInferenceProvider();
        var tools  = new FakeToolRegistry();
        tools.TestOutputs.Enqueue(RedFqn);
        tools.TestOutputs.Enqueue(RedFqn);
        tools.TestOutputs.Enqueue(Green);
        var progress = new List<string>();

        await RunAsync(client, tools, ["/tdd"], capture: new FakeCapture { State = SomeState() },
                       approval: new RecordingApproval(answer: false), onProgress: progress.Add);

        Assert.DoesNotContain(Strings.TddDebugCaptureUnavailable, progress);
    }

    // ── §25: test-file write guard ──────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\proj\tests\CalculatorTests.cs", true)]
    [InlineData(@"C:\proj\src\CalculatorTests.cs", true)]          // name alone suffices
    [InlineData(@"C:\proj\test\helpers.py", true)]
    [InlineData(@"C:\proj\src\__tests__\app.js", true)]
    [InlineData(@"C:\proj\src\app.spec.ts", true)]
    [InlineData(@"C:\proj\src\test_parser.py", true)]
    [InlineData(@"C:\proj\Inferpal.Tests\FakeProvider.cs", true)]  // .NET "<Project>.Tests" folder — this repo's own convention (revue lot 4)
    [InlineData(@"C:\proj\App.Test\Fixtures\data.json", true)]
    [InlineData(@"C:\proj\src\Calculator.cs", false)]
    [InlineData(@"C:\proj\src\Contest.cs", false)]                 // "test" inside a word is not a segment
    [InlineData(@"C:\proj\attestation\Rules.cs", false)]
    [InlineData("", false)]
    public void TestFileWriteGuard_RecognisesTestTargets(string subject, bool expected) =>
        Assert.Equal(expected, TestFileWriteGuard.TargetsTestFile(subject));

    [Fact]
    public async Task TestFileWriteGuard_ForcesThePromptOnTestFiles_AndOnlyThere()
    {
        var inner = new RecordingApproval(answer: true);
        var guard = new TestFileWriteGuard(inner);

        await guard.RequestApprovalAsync("apply_diff", "details", CancellationToken.None,
                                         subject: @"C:\proj\tests\CalculatorTests.cs");
        await guard.RequestApprovalAsync("apply_diff", "details", CancellationToken.None,
                                         subject: @"C:\proj\src\Calculator.cs");

        Assert.True(inner.Asked[0].Forced,  "a test-file write must reach the human whatever the rules say");
        Assert.False(inner.Asked[1].Forced, "production writes keep the normal pipeline");
    }
    // -- A run that executed NOTHING is a third state ---------------------------

    /// <summary>
    /// What `run_tests` writes when the filter matched nothing. Taken from the tool's own constant,
    /// never retyped: a copy here would keep passing after the tool changed its wording.
    /// </summary>
    private static string NoMatch => Services.Tools.RunTestsTool.NoTestMatchedFilter;

    /// <summary>The same, for a runner that exited 0 without a parsable summary.</summary>
    private static string Unproven => Services.Tools.RunTestsTool.NothingProven;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARunThatExecutedNoTest_StopsTheLoop_AndNeverAsksTheModel(bool byFilter)
    {
        // The loop had two states, the product has three. Folded into "failing", a mistyped filter
        // or a runner that proved nothing bought five agent rounds of speculative patches against a
        // report that says nothing ran.
        var client = new FakeInferenceProvider();
        var tools  = new FakeToolRegistry();
        tools.TestOutputs.Enqueue(byFilter ? NoMatch : Unproven);

        var result = await RunAsync(client, tools, ["/tdd", "MyFilter"]);

        Assert.Contains(Strings.TddNothingRan, result.Message, StringComparison.Ordinal);
        Assert.Contains(byFilter ? "No test matched" : "nothing was proven", result.Message,
                        StringComparison.Ordinal);                    // the tool's own words are kept
        Assert.Empty(client.AgentRuns);
        Assert.Single(tools.Calls);                                   // one run, not five
    }

    [Fact]
    public async Task IfTheFailingTestDisappearsMidLoop_TheLoopStops_InsteadOfDeclaringVictory()
    {
        // The dishonest way for a fix loop to go green: make the test stop existing. Round 1 is red,
        // round 2 answers "no test matched" -- which is not a pass, and not a failure either.
        var client = new FakeInferenceProvider();
        var tools  = new FakeToolRegistry();
        tools.TestOutputs.Enqueue(Red);
        tools.TestOutputs.Enqueue(NoMatch);

        var result = await RunAsync(client, tools, ["/tdd", "MyFilter"]);

        Assert.Contains(Strings.TddNothingRan, result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Strings.TddSuccess(2), result.Message, StringComparison.Ordinal);
        Assert.Single(client.AgentRuns);                              // the one real red round
    }

    [Fact]
    public async Task AGenuinelyFailingRun_StillIterates()
    {
        // REFERENCE ARM: without it, a fix that stopped on every non-green report would pass the
        // two tests above and turn /tdd into a one-shot command.
        var client = new FakeInferenceProvider();
        var tools  = new FakeToolRegistry();
        tools.TestOutputs.Enqueue(Red);
        tools.TestOutputs.Enqueue(Green);

        var result = await RunAsync(client, tools, ["/tdd"]);

        Assert.Equal(Strings.TddSuccess(2), result.Message);
        Assert.Single(client.AgentRuns);
    }
}
