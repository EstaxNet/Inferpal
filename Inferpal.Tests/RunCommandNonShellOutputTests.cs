using System.IO;
using System.Text.Json;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Execution;
using Inferpal.Services.Tasks;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A run_command result without an exit note is a command that exited 0 — but only when it IS the shell's output. A
/// registry answering in its place (plan mode, a background task, a call that timed out) and a job poll carry no note
/// either: a <c>run_command {"command":"dotnet test"}</c> refused in plan mode showed "tests passed" on the result bar,
/// and a poll of a job that finished on exit code 1 hid an earlier red run from the end-of-turn notice.
/// </summary>
public class RunCommandNonShellOutputTests
{
    private const string TestRun = """{"command":"dotnet test"}""";

    private sealed class Inner : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
            [new("function", new ToolFunction("run_command", "run", new { })), new("function", new ToolFunction("read_file", "read", new { }))];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => Task.FromResult("ran");
    }

    private static readonly JsonElement Args = JsonSerializer.SerializeToElement(new { command = "dotnet test" });

    [Fact]
    public async Task ARefusalInPlanMode_IsNotAPassingRun()
    {
        var said = await new PlanModeToolRegistry(new Inner()).ExecuteAsync("run_command", Args, CancellationToken.None);

        Assert.Null(CheckCommand.Failed(TestRun, said));
    }

    [Fact]
    public async Task ARefusalInABackgroundTask_IsNotAPassingRun()
    {
        var said = await new BackgroundTaskToolRegistry(new Inner()).ExecuteAsync("run_command", Args, CancellationToken.None);

        Assert.Null(CheckCommand.Failed(TestRun, said));
    }

    [Fact]
    public void APollOfABackgroundJob_IsNotARunOfItsCommand()
    {
        var input = """{"action":"poll","id":"bg1","command":"dotnet test"}""";

        Assert.Null(CheckCommand.Failed(input, "[job 'bg1' — finished (exit code 1)]\nFailed!  - Failed: 2"));
    }

    [Fact]
    public void TheRegistrysOwnTimeoutAndGiveUp_SayError()
    {
        // A call that timed out or gave up ran to no end: like every answer given in place of a run, it says Error.
        var registry = ConventionCoverageTests.CodeOnly(Path.Combine(ConventionCoverageTests.RepoRoot(),
            "Inferpal.Core", "Services", "Execution", "ToolRegistry.cs"));
        Assert.Contains("return $\"Error: tool '{name}' timed out", registry, StringComparison.Ordinal);
        Assert.Contains("return $\"Error: tool '{name}' gave up", registry, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShellsOwnOutput_WithoutANote_StillPasses()
    {
        // Reference arm: the shell writes no note for exit code 0.
        Assert.False(CheckCommand.Failed(TestRun, "Passed!  - Failed: 0, Passed: 12"));
    }
}
