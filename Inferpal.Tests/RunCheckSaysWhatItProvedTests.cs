using Inferpal.Localization;
using Inferpal.Services.Agent;
using Inferpal.Services.Presentation;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The run's line and its result bar say what the last check proved, and no more: a build stopped before the compiler
/// carries no ✓, and a filtered test run says it was part of the suite.
/// </summary>
public sealed class RunCheckSaysWhatItProvedTests
{
    private static ToolExecution Run(string tool, string input, string output) => new(tool, input, output);

    private static readonly string NotRestored = GetDiagnosticsTool.NotRestored + "\nerror NETSDK1004: assets file not found";

    [Fact]
    public void ABuildStoppedBeforeTheCompiler_ProvesNothing_AndSaysSo()
    {
        var summary = RunSummary.Build([Run("get_diagnostics", "{}", NotRestored)], null)!;

        Assert.Equal(RunCheck.Unproven, summary.Check);
        Assert.Equal(Strings.RunNotVerified, summary.CheckText);
        Assert.Contains(Strings.RunNotVerified, summary.Detail);
    }

    [Fact]
    public void AnEditWhoseBuildCheckCouldNotRun_ProvesNothing_AndSaysSo()
    {
        var note = Strings.SmartFixCouldNotRun("a project is not restored");

        var summary = RunSummary.Build([Run("apply_diff", "{}", "Applied.\n" + note)], null)!;

        Assert.Equal(RunCheck.Unproven, summary.Check);
    }

    /// <summary>Reference arm: a turn with no check at all says nothing about one.</summary>
    [Fact]
    public void ARunWithoutACheck_SaysNothing()
    {
        var summary = RunSummary.Build([Run("read_file", "{}", "content")], null)!;

        Assert.Equal(RunCheck.None, summary.Check);
        Assert.Equal(string.Empty, summary.CheckText);
    }

    /// <summary>A check that proved something after one that did not keeps its verdict.</summary>
    [Fact]
    public void AProvenCheck_WinsOverAnUnprovenOne()
    {
        var summary = RunSummary.Build(
            [Run("get_diagnostics", "{}", NotRestored), Run("run_tests", "{}", "✓ Tests passed (dotnet): 3 passed")], null)!;

        Assert.Equal(RunCheck.TestsPassed, summary.Check);
    }

    [Fact]
    public void AFilteredTestRun_SaysItWasPartOfTheSuite()
    {
        var summary = RunSummary.Build([Run("run_tests", """{"filter":"OneTest"}""", "✓ Tests passed (dotnet): 1 passed")], null)!;

        Assert.Equal(RunCheck.TestsPassedPart, summary.Check);
        Assert.Equal(Strings.RunTestsPassedPart, summary.CheckText);
    }

    [Fact]
    public void AFilteredTestRunThatFailed_StaysFailed()
    {
        var failed = "✗ Tests failed (dotnet): 1 failed";

        var summary = RunSummary.Build([Run("run_tests", """{"filter":"OneTest"}""", failed)], null)!;

        Assert.Equal(RunCheck.TestsFailed, summary.Check);
    }

    /// <summary>The value crosses to VS Code as text: its name there is the enum's, lower-cased first.</summary>
    [Fact]
    public void VsCode_DrawsAnUnprovenRunWithoutItsCheckMark()
    {
        var protocol = WebviewRebuildTests.TsCode("protocol.ts");
        var main     = WebviewRebuildTests.TsCode("webview/main.ts");

        Assert.Contains("'testsPassedPart' | 'unproven' | 'passedThenEdited'", protocol);
        Assert.Contains("const info = unproven || run.check === 'passedThenEdited';", main);
        Assert.Contains("failed ? 'failed' : info ? 'info' : 'check'", main);
        Assert.Contains("failed ? 'failed' : run.check === 'unproven' || run.check === 'passedThenEdited' ? 'info' : 'check'", main);
        // A failed step turns only a run with no check, or nothing proven, red — never a green followed by edits.
        Assert.Contains("const unproven = run.check === 'unproven';", main);
        Assert.Contains("((run.check === 'none' || unproven) && stepFailed)", main);
    }
}
