using Inferpal.Services.Commands;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A run whose every selected test was skipped executed nothing — and its report has a total and no
/// failure, which a summary parser reads as green. Skipping the failing test is the dishonest way for a
/// fix loop to go green: on the <c>dotnet test --verbosity normal</c> block that <c>run_tests</c> asks
/// for, <c>/tdd</c> declared victory on the round that added <c>Skip = "…"</c>. The outputs are the
/// runners' real ones (SDK 10 and pytest 9.1, captured).
/// </summary>
public class RunnerOnlySkippedTests
{
    private static void AssertNothingRan(string report)
    {
        Assert.Contains(RunTestsTool.OnlySkipped, report);
        Assert.True(TddCommandHandler.NothingRan(report), report);
        Assert.False(TddCommandHandler.TestsPassed(report), report);
        Assert.DoesNotContain("✓", report);
    }

    [Fact]
    public void Dotnet_TheVerbosityNormalBlock_WithEveryTestSkipped_IsNotAPass()
    {
        var report = RunTestsTool.ParseDotnetOutput("""
            [xUnit.net 00:00:00.15]   Finished:    SkipX
              Skipped SkipX.UnitTest1.Broken [1 ms]

            Test Run Successful.
            Total tests: 1
                Skipped: 1
             Total time: 0,5925 Seconds
            """, 0);

        AssertNothingRan(report);
        Assert.Contains("Skipped: 1, Total: 1", report);
    }

    [Fact]
    public void Dotnet_TheSkippedSummaryLine_IsReadAsSuch_NotAsAnUnparsableRun()
    {
        // "Skipped!" was not a summary to the parser: the report said no summary could be parsed —
        // true of nothing here, and it sent the reader to the runner's output format.
        var report = RunTestsTool.ParseDotnetOutput(
            "Skipped! - Failed:     0, Passed:     0, Skipped:     1, Total:     1, Duration: 1 ms - SkipX.dll (net10.0)\n", 0);

        AssertNothingRan(report);
        Assert.DoesNotContain(RunTestsTool.NothingProven, report);
    }

    [Fact]
    public void Dotnet_ASkippedProjectNextToAPassingOne_IsCounted()
    {
        var report = RunTestsTool.ParseDotnetOutput(
            "Passed! - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 9 ms - A.Tests.dll (net8.0)\n" +
            "Skipped! - Failed:     0, Passed:     0, Skipped:     1, Total:     1, Duration: 1 ms - B.Tests.dll (net8.0)\n", 0);

        Assert.Contains("Passed: 5, Skipped: 1, Total: 6", report);
        Assert.True(TddCommandHandler.TestsPassed(report), report);        // tests DID run
    }

    [Fact]
    public void Cargo_EveryMatchedTestIgnored_IsNotAPass()
    {
        AssertNothingRan(RunTestsTool.ParseCargoOutput(
            "running 1 test\ntest broken ... ignored\n\ntest result: ok. 0 passed; 0 failed; 1 ignored; 0 measured; 4 filtered out; finished in 0.00s\n", 0));
    }

    [Fact]
    public void Pytest_EveryCollectedTestSkipped_IsNothingRan_NotAFailure()
    {
        var report = RunTestsTool.ParsePytestOutput("""
            tests\test_y.py s                                                        [100%]

            ====================== 1 skipped, 2 deselected in 0.01s =======================
            """, 0);

        AssertNothingRan(report);
        Assert.Contains("1 skipped, 2 deselected", report);
    }

    // ── Reference arm: a skip next to tests that ran keeps its verdict ───────────

    [Fact]
    public void ASkipNextToTestsThatRan_KeepsTheVerdict()
    {
        var dotnet = RunTestsTool.ParseDotnetOutput("Test Run Successful.\nTotal tests: 5\n     Passed: 4\n    Skipped: 1\n", 0);
        var pytest = RunTestsTool.ParsePytestOutput("================== 1 passed, 1 skipped in 0.01s ==================\n", 0);
        var cargo  = RunTestsTool.ParseCargoOutput("test result: FAILED. 0 passed; 1 failed; 2 ignored; 0 measured; 0 filtered out; finished in 0.00s\n", 101);

        foreach (var green in new[] { dotnet, pytest })
        {
            Assert.True(TddCommandHandler.TestsPassed(green), green);
            Assert.False(TddCommandHandler.NothingRan(green), green);
        }
        Assert.False(TddCommandHandler.TestsPassed(cargo), cargo);
        Assert.False(TddCommandHandler.NothingRan(cargo), cargo);
    }
}
