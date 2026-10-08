using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Agent;
using Inferpal.Services.Presentation;
using Inferpal.Services.Shell;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  The exit code of a test or build run through the shell is its verdict only when it is ITS code.
//
//  Under bash, `pytest -q | tail -30` exits with tail's 0 whatever the tests said; `pytest; echo done`
//  and `pytest || true` end on the last command's code (measured: bash `false | tail -1` → 0;
//  PowerShell keeps the native code through a cmdlet — Select-Object, Tee-Object → 3 — but not
//  through a native filter — findstr → 1). Read as the check's, a red run showed "tests passed" on
//  the run's result bar. And the code the session reported was missed when a note followed it (a
//  background process holding the output) or one stood above it (the session's folder had vanished).
// ──────────────────────────────────────────────────────────────────────────────────────────────
[Collection(CultureSerialCollection.Name)]   // reads a localized Smart Fix note
public sealed class ShellCheckExitCodeOwnerTests
{
    private static ToolExecution Shell(string command, string output) =>
        new("run_command", System.Text.Json.JsonSerializer.Serialize(new { command }), output);

    private const string PytestRedThroughTail = "FAILED tests/test_cart.py::test_total - assert 3 == 4\n1 failed, 2 passed in 0.05s";

    [Theory]
    [InlineData("pytest -q | tail -30")]
    [InlineData("dotnet test 2>&1 | tail -n 40")]
    [InlineData("dotnet test | findstr Failed")]
    [InlineData("pytest; echo done")]
    [InlineData("cargo test || true")]
    [InlineData("npm test &")]
    public void ACheckFollowedByACommandThatOwnsTheExitCode_ProvesNothing(string command)
    {
        Assert.Null(CheckCommand.Failed(System.Text.Json.JsonSerializer.Serialize(new { command }), PytestRedThroughTail));
    }

    [Fact]
    public void ARedRunPipedThroughTail_IsNeverTheBarsTestsPassed()
    {
        var turn = new[]
        {
            new ToolExecution("apply_diff", "{}", "Applied 1 edit.\n\n" + Strings.SmartFixBuildOk),
            Shell("python -m pytest -q | tail -30", PytestRedThroughTail),
        };

        Assert.NotEqual(RunCheck.TestsPassed, RunSummary.LastCheck(turn));
    }

    [Theory]
    [InlineData("dotnet test | Select-Object -Last 30")]
    [InlineData("dotnet test | Tee-Object -FilePath log.txt")]
    [InlineData("cd tests && pytest -q && echo ok")]
    [InlineData("dotnet test --filter \"FullyQualifiedName~A|FullyQualifiedName~B\"")]
    [InlineData("dotnet test 2>&1")]
    public void ACheckThatKeepsItsExitCode_IsJudgedOnIt(string command)
    {
        // Reference arm: a cmdlet, an && chain, a "|" inside quotes and a redirection leave the check's code in place.
        Assert.True(CheckCommand.Failed(System.Text.Json.JsonSerializer.Serialize(new { command }), "1 failed\n[exit code 1]"));
        Assert.False(CheckCommand.Failed(System.Text.Json.JsonSerializer.Serialize(new { command }), "3 passed"));
    }

    [Fact]
    public void TheExitCode_IsReadUnderTheNoteOfAProcessHoldingTheOutput()
    {
        var output = "1 failed\n[exit code 1]" + ChildProcess.OutputHeldOpenNote;

        Assert.Equal(1, CheckCommand.ExitCode(output));
    }

    [Fact]
    public void ATimeout_UnderTheVanishedFolderNote_IsNoVerdict()
    {
        var timedOut = ChildProcess.TimedOutMessage(120, "1 failed");
        var output   = ShellSession.VanishedNote("/ws/build", "/ws") + timedOut;

        Assert.Null(CheckCommand.ExitCode(output));
        Assert.Equal(timedOut, ShellSession.WithoutNotes(output));        // the note, and nothing else, comes off
    }
}
