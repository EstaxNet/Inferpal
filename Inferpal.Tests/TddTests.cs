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

// /tdd (ROADMAP 1.5.0 §10, pulled forward): the fix-until-green loop — verdict parsing across
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
        Action<string, bool>? onTestReport = null, string? systemPrompt = null) =>
        TddCommandHandler.HandleAsync(
            client, new InferpalConfig { DefaultModel = "big", AgentModel = "coder" },
            tools, systemPrompt, parts, projectRoot: @"C:\proj",
            onProgress: null, onTestReport, onStep: null, onToken: null, onFixResult: null,
            CancellationToken.None);

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
}
