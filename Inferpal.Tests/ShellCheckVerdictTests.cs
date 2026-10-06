using Inferpal.Localization;
using Inferpal.Services.Agent;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A test or build run through run_command is the last check of a turn like run_tests.
//
//  On the battery a model renamed a Python method by hand, ran pytest through the shell — an
//  IndentationError, exit 1 — and answered that the rename was applied; the turn ended without a
//  word, because only run_tests, get_diagnostics and Smart Fix counted as checks. Judged on the exit
//  code the shell reported, never on the words of the output.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class ShellCheckVerdictTests
{
    private static ToolExecution Shell(string command, string output) =>
        new("run_command", System.Text.Json.JsonSerializer.Serialize(new { command }), output);

    private static ToolExecution Ran(string name, string output = "OK") => new(name, "{}", output);

    private const string PytestRed = "E   IndentationError: unexpected indent\n1 error in 0.05s\n[exit code 1]";
    private const string PytestGreen = "3 passed in 0.04s";

    [Theory]
    [InlineData("pytest -q")]
    [InlineData("python -m pytest tests/")]
    [InlineData("cd src && dotnet test")]
    [InlineData("npm test")]
    [InlineData("npx jest cart")]
    [InlineData("cargo test")]
    [InlineData("go test ./...")]
    [InlineData("CI=1 npm run test")]
    public void TestAndBuildRuns_AreChecks(string command) => Assert.True(CheckCommand.IsCheck(command));

    [Theory]
    [InlineData("echo pytest")]
    [InlineData("grep -rn dotnet test .")]
    [InlineData("git status")]
    [InlineData("cat tests/test_cart.py")]
    public void CommandsThatOnlyMentionARunner_AreNot(string command) => Assert.False(CheckCommand.IsCheck(command));

    [Fact]
    public void AFailingShellTestRun_LastInTheTurn_IsSaid()
    {
        Assert.True(ChatTurnPolicy.LastCheckFailed(
            [Ran("apply_edits"), Shell("python -m pytest -q", PytestRed)], filesChangedInRun: 1));
    }

    [Fact]
    public void APassingShellTestRun_AfterAFailingOne_IsTheLastWord()
    {
        // Reference arms: red then green says nothing; a failing command that is not a check changes nothing.
        Assert.False(ChatTurnPolicy.LastCheckFailed(
            [Ran("apply_edits"), Shell("pytest", PytestRed), Shell("pytest", PytestGreen)], filesChangedInRun: 1));
        Assert.False(ChatTurnPolicy.LastCheckFailed(
            [Ran("apply_edits"), Shell("pytest", PytestGreen), Shell("grep -rn compute_total .", "[exit code 1]")],
            filesChangedInRun: 1));
    }

    [Fact]
    public void ACheckThatDidNotRun_IsNotAVerdict()
    {
        // A refused command, an error before the run, a background job: none is a green run hiding an earlier red one.
        var red = Ran("run_tests", "1 failed, 2 passed in 0.01s");
        Assert.True(ChatTurnPolicy.LastCheckFailed([Ran("apply_edits"), red, Shell("pytest", Strings.RunCancelled)], 1));
        Assert.True(ChatTurnPolicy.LastCheckFailed([Ran("apply_edits"), red, Shell("pytest", "Error: 'working_directory' x is not an existing folder")], 1));
        Assert.True(ChatTurnPolicy.LastCheckFailed([Ran("apply_edits"), red, Shell("pytest", "Started background job 'bg1'. Use action='poll'…")], 1));
    }

    [Fact]
    public void ATurnThatChangedNothing_StillSaysNothing()
    {
        // The existing rule holds: "does it pass?" with no edit reports the state, the notice would contradict it.
        Assert.False(ChatTurnPolicy.LastCheckFailed([Shell("pytest", PytestRed)], filesChangedInRun: 0));
    }
}
