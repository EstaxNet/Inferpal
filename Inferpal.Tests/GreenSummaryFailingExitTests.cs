using Inferpal.Services.Commands;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A summary is the verdict of the tests that ran, never of the run. pytest exits 1 under a green summary when a plugin
/// fails the session (pytest-cov's fail_under), cargo exits 101 when a later test binary aborts after an earlier one
/// printed "ok": read from the summary alone both were green — /tdd declared victory, the result bar said "tests
/// passed". npm and dotnet already read the exit code; pytest and cargo now do too. And a pytest run longer than a minute
/// prints its duration twice ("in 75.32s (0:01:15)"), which the summary pattern did not read: "nothing was proven".
/// </summary>
public class GreenSummaryFailingExitTests
{
    [Fact]
    public void Pytest_AGreenSummaryWithAFailingExitCode_IsRed_AndSaysWhy()
    {
        var raw = "tests/test_cart.py ...                                   [100%]\n"
                + "=============================== 3 passed in 0.42s ===============================\n"
                + "FAIL Required test coverage of 90% not reached. Total coverage: 80.00%\n";

        var report = RunTestsTool.ParsePytestOutput(raw, exitCode: 1);

        Assert.True(TddCommandHandler.TestsFailed(report));
        Assert.False(TddCommandHandler.TestsPassed(report));
        Assert.Contains("Required test coverage", report);   // the reason travels with the verdict
    }

    [Fact]
    public void Pytest_ARunLongerThanAMinute_IsReadLikeAnyOther()
    {
        var raw = "======================== 3 passed in 75.32s (0:01:15) =========================\n";

        var report = RunTestsTool.ParsePytestOutput(raw, exitCode: 0);

        Assert.True(TddCommandHandler.TestsPassed(report));
        Assert.False(TddCommandHandler.NothingRan(report));
    }

    [Fact]
    public void Cargo_AGreenSummaryWithAFailingExitCode_IsRed()
    {
        var raw = "running 4 tests\n"
                + "test result: ok. 4 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.01s\n\n"
                + "     Running tests/it.rs (target/debug/deps/it-1a2b)\n"
                + "thread 'main' has overflowed its stack\nfatal runtime error: stack overflow\n"
                + "error: test failed, to rerun pass `--test it`\n";

        var report = RunTestsTool.ParseCargoOutput(raw, exitCode: 101);

        Assert.True(TddCommandHandler.TestsFailed(report));
        Assert.Contains("stack overflow", report);
    }

    [Fact]
    public void AGreenSummaryWithAZeroExitCode_StaysGreen()
    {
        // Reference arms: the summaries that passed, with a passing exit code.
        Assert.True(TddCommandHandler.TestsPassed(RunTestsTool.ParsePytestOutput("===== 3 passed in 0.42s =====\n", 0)));
        Assert.True(TddCommandHandler.TestsPassed(RunTestsTool.ParseCargoOutput(
            "test result: ok. 4 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out\n", 0)));
    }
}
