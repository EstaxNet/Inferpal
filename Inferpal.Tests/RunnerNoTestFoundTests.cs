using Inferpal.Services.Commands;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// cargo, go and pytest each SAY when they found no test to run, and each says it in a form a
/// summary parser reads as a verdict: cargo's <c>test result: ok. 0 passed; 0 failed</c> and go's
/// <c>ok … [no tests to run]</c> were a green <c>/tdd</c> declared victory on — including the round
/// where the failing test was renamed away —, pytest's exit 5 <c>2 deselected</c> a red it spent
/// five rounds fixing code against. The outputs are the runners' real ones (pytest 9.1 captured;
/// cargo's summary line and go's suffix as their sources print them).
/// </summary>
public class RunnerNoTestFoundTests
{
    private static void AssertNothingRan(string report)
    {
        Assert.True(TddCommandHandler.NothingRan(report), report);
        Assert.False(TddCommandHandler.TestsPassed(report), report);
        Assert.DoesNotContain("✓", report);
    }

    [Fact]
    public void Cargo_AFilterThatMatchesNothing_IsNothingRan_NotAPass()
    {
        var report = RunTestsTool.ParseCargoOutput("""
            running 0 tests

            test result: ok. 0 passed; 0 failed; 0 ignored; 0 measured; 5 filtered out; finished in 0.00s

               Doc-tests demo

            running 0 tests

            test result: ok. 0 passed; 0 failed; 0 ignored; 0 measured; 1 filtered out; finished in 0.00s
            """, 0);

        AssertNothingRan(report);
        Assert.Contains(RunTestsTool.NoTestMatchedFilter, report);
        Assert.Contains("Filtered out: 6", report);
    }

    [Fact]
    public void Cargo_ACrateWithNoTest_IsNothingRan()
    {
        var report = RunTestsTool.ParseCargoOutput(
            "running 0 tests\n\ntest result: ok. 0 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.00s\n", 0);

        AssertNothingRan(report);
        Assert.Contains(RunTestsTool.NoTestFound, report);
    }

    [Fact]
    public void Go_AnOkLineWithNoTestsToRun_IsNothingRan_NotAPass()
    {
        var report = RunTestsTool.ParseGoOutput("""
            ok  	example.com/app/parser	0.004s [no tests to run]
            ok  	example.com/app/lexer	(cached) [no tests to run]
            ?   	example.com/app/cmd	[no test files]
            """, 0);

        AssertNothingRan(report);
        Assert.Contains(RunTestsTool.NoTestFound, report);
    }

    [Fact]
    public void Go_NoTestFilesAnywhere_IsNothingRan()
    {
        // Already not green — but read as RED by /tdd, which then patched code for five rounds.
        var report = RunTestsTool.ParseGoOutput("?       example.com/pkg  [no test files]\n?       example.com/cmd  [no test files]\n", 0);

        AssertNothingRan(report);
    }

    [Fact]
    public void Pytest_AFilterThatDeselectsEverything_IsNothingRan_NotAFailure()
    {
        var report = RunTestsTool.ParsePytestOutput("""
            ============================= test session starts =============================
            collected 2 items / 2 deselected / 0 selected

            ============================ 2 deselected in 0.01s ============================
            """, 5);

        AssertNothingRan(report);
        Assert.Contains(RunTestsTool.NoTestMatchedFilter, report);
        Assert.Contains("2 deselected", report);                        // the runner's own words stay
    }

    [Fact]
    public void Pytest_NoTestCollected_IsNothingRan()
    {
        var report = RunTestsTool.ParsePytestOutput("""
            collected 0 items

            ============================ no tests ran in 0.01s ============================
            """, 5);

        AssertNothingRan(report);
        Assert.Contains(RunTestsTool.NoTestFound, report);
    }

    [Fact]
    public void Pytest_AFilterItCannotParse_SaysWhy_AndIsNothingRan()
    {
        // Exit 4 is pytest's usage error. The report kept only "no tests ran" — the reason, on the first line of the
        // output, never reached the model — and /tdd, reading red, patched code against a malformed -k.
        var report = RunTestsTool.ParsePytestOutput("""
            ERROR: Wrong expression passed to '-k': test_a and: at column 11: expected not OR left parenthesis OR identifier; got end of input

            ============================= test session starts =============================
            collected 3 items

            ============================ no tests ran in 0.01s ============================
            """, 4);

        AssertNothingRan(report);
        Assert.Contains("Wrong expression passed to '-k'", report);
    }

    // ── Reference arms: a run where a test DID run keeps its verdict ─────────────

    [Fact]
    public void AFilterThatMatchesSomeTests_KeepsItsVerdict()
    {
        var cargo  = RunTestsTool.ParseCargoOutput("test result: ok. 2 passed; 0 failed; 0 ignored; 0 measured; 3 filtered out; finished in 0.00s\n", 0);
        var go     = RunTestsTool.ParseGoOutput("ok  \texample.com/app/parser\t0.004s\nok  \texample.com/app/lexer\t0.003s [no tests to run]\n", 0);
        var pytest = RunTestsTool.ParsePytestOutput("======================= 1 passed, 1 deselected in 0.01s =======================\n", 0);
        var cargoRed = RunTestsTool.ParseCargoOutput("test result: FAILED. 0 passed; 1 failed; 0 ignored; 0 measured; 3 filtered out; finished in 0.00s\n", 101);

        foreach (var green in new[] { cargo, go, pytest })
        {
            Assert.True(TddCommandHandler.TestsPassed(green), green);
            Assert.False(TddCommandHandler.NothingRan(green), green);
        }
        Assert.False(TddCommandHandler.TestsPassed(cargoRed), cargoRed);
        Assert.False(TddCommandHandler.NothingRan(cargoRed), cargoRed);
    }
}
