using Inferpal.Localization;
using Inferpal.Services.Agent;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Proven or guessed (pari 148): whether a run that changed files showed the bug first — a check that FAILED before its
/// first edit — read from what the run did, never from its answer.
/// </summary>
public class ReproductionTests
{
    private static ToolExecution Ran(string name, string output = "output", string input = "{}") => new(name, input, output);
    private const string TestsRed = "✗ 1 failed, 41 passed";
    private const string TestsGreen = "✓ 42 passed";
    private static ToolExecution Edit() => Ran("apply_diff", "Edited src/Ods.cs.") with { Wrote = true };

    [Fact]
    public void ATestThatFailedBeforeTheFirstEdit_Reproduced()
    {
        var run = new[] { Ran("read_file"), Ran("run_tests", TestsRed), Edit(), Ran("run_tests", TestsGreen) };
        Assert.Equal(ChatTurnPolicy.ReproductionState.Reproduced, ChatTurnPolicy.Reproduction(run));
    }

    [Fact]
    public void AGreenRunBeforeTheEdit_IsNoReproduction()
    {
        // REFERENCE ARM: the same run whose first test run passed showed nothing of the bug.
        var run = new[] { Ran("read_file"), Ran("run_tests", TestsGreen), Edit(), Ran("run_tests", TestsGreen) };
        Assert.Equal(ChatTurnPolicy.ReproductionState.NotReproduced, ChatTurnPolicy.Reproduction(run));
    }

    [Fact]
    public void ARedRunAfterTheFirstEdit_IsNoReproduction()
    {
        var run = new[] { Ran("read_file"), Edit(), Ran("run_tests", TestsRed), Edit(), Ran("run_tests", TestsGreen) };
        Assert.Equal(ChatTurnPolicy.ReproductionState.NotReproduced, ChatTurnPolicy.Reproduction(run));
    }

    [Fact]
    public void ARunThatChangedNothing_SaysNothing() =>
        Assert.Equal(ChatTurnPolicy.ReproductionState.None,
                     ChatTurnPolicy.Reproduction([Ran("read_file"), Ran("run_tests", TestsRed)]));

    [Fact]
    public void ABugReportsFix_SaysProvenOrGuessed_OnTheResultBar()
    {
        var proven = RunSummary.Build([Ran("run_tests", TestsRed), Edit(), Ran("run_tests", TestsGreen)], null, bugReport: true)!;
        Assert.Equal(RunCheck.TestsPassed, proven.Check);
        Assert.Equal(Strings.RunReproducedThenFixed(Strings.RunTestsPassed), proven.CheckText);
        Assert.Equal(Strings.RunReproducedThenFixedTip, proven.CheckTip);

        var guessed = RunSummary.Build([Ran("read_file"), Edit(), Ran("run_tests", TestsGreen)], null, bugReport: true)!;
        Assert.Equal(RunCheck.PassedUnreproduced, guessed.Check);
        Assert.Equal(Strings.RunFixedWithoutReproducing(Strings.RunTestsPassed), guessed.CheckText);

        var stillRed = RunSummary.Build([Ran("run_tests", TestsRed), Edit(), Ran("run_tests", TestsRed)], null, bugReport: true)!;
        Assert.Equal(RunCheck.TestsFailed, stillRed.Check);
        Assert.Equal(Strings.RunReproducedTestsStillFail, stillRed.CheckText);
    }

    [Fact]
    public void AnythingButABugReport_KeepsTodaysBar()
    {
        // REFERENCE ARM: the same run asked as a feature, and a bug report that changed nothing.
        var feature = RunSummary.Build([Ran("read_file"), Edit(), Ran("run_tests", TestsGreen)], null, bugReport: false)!;
        Assert.Equal(RunCheck.TestsPassed, feature.Check);
        Assert.Equal(Strings.RunTestsPassed, feature.CheckText);
        Assert.Equal("", feature.CheckTip);
        var nothingChanged = RunSummary.Build([Ran("run_tests", TestsRed)], null, bugReport: true)!;
        Assert.Equal(Strings.RunTestsFailed, nothingChanged.CheckText);
    }

    [Fact]
    public void NoClaimAboutTheFix_StandsOnACheckThatDidNotRunAfterIt()
    {
        // A red run, then an edit nothing checked: never "still fails" — the fix was never tested.
        var unchecked_ = RunSummary.Build([Ran("run_tests", TestsRed), Edit()], null, bugReport: true)!;
        Assert.Equal(Strings.RunTestsFailed, unchecked_.CheckText);
        Assert.Equal("", unchecked_.CheckTip);
    }

    [Fact]
    public void ARedTest_IsNotFixedByAGreenBuild()
    {
        var build = Ran("get_diagnostics", "Build succeeded: no errors.");
        var summary = RunSummary.Build([Ran("run_tests", TestsRed), Edit(), build], null, bugReport: true)!;
        Assert.DoesNotContain(Strings.RunReproducedThenFixed(""), summary.CheckText);
    }

    [Fact]
    public void ACheckThatWroteFiles_StillReproduces()
    {
        // A first test run that wrote its snapshots is a check before it is a change.
        var run = new[] { Ran("run_tests", TestsRed) with { Wrote = true }, Edit(), Ran("run_tests", TestsGreen) };
        Assert.Equal(ChatTurnPolicy.ReproductionState.Reproduced, ChatTurnPolicy.Reproduction(run));
    }
}
