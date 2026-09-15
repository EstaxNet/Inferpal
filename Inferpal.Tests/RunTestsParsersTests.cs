using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// Output parsing for the cargo (Rust) and go test runners added alongside the polyglot Smart Fix.
public class RunTestsParsersTests
{
    // ── dotnet (modern vstest block, SDK 9/10) ─────────────────────────────────
    // The single-line "Passed! - Failed: …" format the parser originally targeted is gone from
    // modern SDKs: every run fell through to "no summary line detected" (green) or the raw
    // MSBuild dump (red). Found by an internal measurement campaign (2026-08-20) — both tests
    // below are red without the multi-line block parsing.

    [Fact]
    public void Dotnet_ModernBlock_AllPassing_ReportsParsedSummary()
    {
        var raw = """
            Test run for C:\x\Cobaye.Tests.dll (.NETCoreApp,Version=v8.0)
              Passed Cobaye.Tests.SortingTests.SortsSmallestFirst [9 ms]

            Test Run Successful.
            Total tests: 16
                 Passed: 16
             Total time: 0,3985 Seconds
            """;
        var result = RunTestsTool.ParseDotnetOutput(raw, 0);

        Assert.Contains("✓ PASSED", result);
        Assert.Contains("Total: 16", result);
        Assert.DoesNotContain("no summary line detected", result);
    }

    [Fact]
    public void Dotnet_ModernBlock_WithFailures_ReportsFailedSummaryAndNames()
    {
        var raw = """
              Failed Cobaye.Tests.CalculatorTests.AddsTwoNumbers [12 ms]
              Error Message:
               Assert.Equal() Failure: Expected: 5 / Actual: -1

            Test Run Failed.
            Total tests: 2
                 Passed: 1
                 Failed: 1
             Total time: 0,4 Seconds
            """;
        var result = RunTestsTool.ParseDotnetOutput(raw, 1);

        Assert.Contains("✗ FAILED", result);
        Assert.Contains("Total: 2", result);
        Assert.Contains("✗ Cobaye.Tests.CalculatorTests.AddsTwoNumbers", result);
        Assert.Contains("Assert.Equal() Failure", result);
    }

    [Fact]
    public void Dotnet_ZeroMatchFilter_IsNotReportedGreen()
    {
        // Exit 0 with zero matched tests: an agent that renames or deletes the failing test must
        // not turn the /tdd loop green on a run where nothing ran.
        var raw = """
            No test matches the given testcase filter `FullyQualifiedName~RenamedTests` in C:\x\Cobaye.Tests.dll
              0 Warning(s)
            """;
        var result = RunTestsTool.ParseDotnetOutput(raw, 0);

        Assert.Contains("No test matched the filter", result);
        Assert.DoesNotContain("✓", result);
        Assert.False(Services.Commands.TddCommandHandler.TestsPassed(result),
                     "the /tdd loop must not read a zero-match run as green");
    }

    // ── Cargo ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Cargo_AllPassing_ReportsPassedSummary()
    {
        var raw = """
            running 2 tests
            test tests::it_works ... ok
            test tests::another ... ok

            test result: ok. 2 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out
            """;
        var result = RunTestsTool.ParseCargoOutput(raw, 0);

        Assert.Contains("✓ PASSED", result);
        Assert.Contains("Passed: 2", result);
        Assert.Contains("Failed: 0", result);
        Assert.DoesNotContain("Failing tests:", result);
    }

    [Fact]
    public void Cargo_WithFailures_ListsFailingTestsAndAggregates()
    {
        var raw = """
            running 3 tests
            test tests::it_works ... ok
            test tests::it_fails ... FAILED
            test tests::other_fail ... FAILED

            failures:
                tests::it_fails
                tests::other_fail

            test result: FAILED. 1 passed; 2 failed; 0 ignored; 0 measured; 0 filtered out
            """;
        var result = RunTestsTool.ParseCargoOutput(raw, 101);

        Assert.Contains("✗ FAILED", result);
        Assert.Contains("Failed: 2", result);
        Assert.Contains("Passed: 1", result);
        Assert.Contains("tests::it_fails", result);
        Assert.Contains("tests::other_fail", result);
    }

    [Fact]
    public void Cargo_MultipleBinaries_SumsSummaries()
    {
        var raw = """
            test result: ok. 3 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out
            test result: FAILED. 2 passed; 1 failed; 0 ignored; 0 measured; 0 filtered out
            """;
        var result = RunTestsTool.ParseCargoOutput(raw, 101);

        Assert.Contains("Passed: 5", result);
        Assert.Contains("Failed: 1", result);
        Assert.Contains("Total: 6", result);
    }

    // ── Go ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Go_AllPassing_ReportsPassed()
    {
        var raw = """
            ok      example.com/pkg  0.012s
            ok      example.com/pkg2 0.005s
            """;
        var result = RunTestsTool.ParseGoOutput(raw, 0);

        Assert.Contains("✓ Tests passed", result);
        Assert.DoesNotContain("Failing tests:", result);
        Assert.True(Services.Commands.TddCommandHandler.TestsPassed(result));
    }

    [Fact]
    public void Go_WithFailures_ListsFailingTestsAndDetail()
    {
        var raw = """
            --- FAIL: TestFoo (0.00s)
                foo_test.go:10: expected 1 got 2
            FAIL
            exit status 1
            FAIL    example.com/pkg  0.012s
            """;
        var result = RunTestsTool.ParseGoOutput(raw, 1);

        Assert.Contains("✗ FAILED", result);
        Assert.Contains("TestFoo", result);
        Assert.Contains("foo_test.go:10", result);
    }

    // ── "exit 0 = green": the fallback 1.5.2 closed on ONE case ────────────────
    //
    // The 1.5.2 patch wrote the rule - never a silent exit-0-means-green fallback - and applied it
    // to the single vstest "no test matched the filter" case. The GENERAL fallback stayed in all
    // four parsers: unreadable summary + zero exit code => "✓ Tests passed". The chain runs all
    // the way through - LooksLikeTestReport lets it past, TestsPassed reads the leading ✓, /tdd
    // returns "test suite green" - so the loop can announce a victory nobody measured.
    // One test per runner, each seen red without its fix.

    [Fact]
    public void Dotnet_ExitZeroWithoutSummary_IsNotReportedGreen()
    {
        // The case that actually happens: the vstest summary block changes shape (SDK 8 -> 9), or
        // the solution holds no test project at all. `dotnet test` returns 0 and prints no
        // summary this parser can read.
        var result = RunTestsTool.ParseDotnetOutput("Determining projects to restore...\n  0 Warning(s)\n", 0);

        Assert.DoesNotContain("✓", result);
        Assert.False(Services.Commands.TddCommandHandler.TestsPassed(result),
                     "an unreadable summary proves nothing: /tdd must keep going, not conclude");
    }

    [Fact]
    public void Pytest_ExitZeroWithoutSummary_IsNotReportedGreen()
    {
        var result = RunTestsTool.ParsePytestOutput("collected 0 items\n", 0);

        Assert.DoesNotContain("✓", result);
        Assert.False(Services.Commands.TddCommandHandler.TestsPassed(result));
    }

    [Fact]
    public void Cargo_ExitZeroWithoutSummary_IsNotReportedGreen()
    {
        var result = RunTestsTool.ParseCargoOutput("   Compiling demo v0.1.0\n    Finished test profile\n", 0);

        Assert.DoesNotContain("✓", result);
        Assert.False(Services.Commands.TddCommandHandler.TestsPassed(result));
    }

    [Fact]
    public void Go_ExitZeroWithNoPackageThatRan_IsNotReportedGreen()
    {
        // go is the only runner whose exit code IS the verdict - but not when nothing ran:
        // "[no test files]" everywhere still returns 0, and that is the easiest false green to
        // produce (a `go test` fired at the root of a repository that has no tests).
        var raw = """
            ?       example.com/pkg  [no test files]
            ?       example.com/cmd  [no test files]
            """;
        var result = RunTestsTool.ParseGoOutput(raw, 0);

        Assert.DoesNotContain("✓", result);
        Assert.False(Services.Commands.TddCommandHandler.TestsPassed(result));
    }
    // ── The count above the list is never the length of the list ────────────────
    //
    // The "Failing tests:" list is a sample, capped at thirty. The COUNT is not, and go's parser
    // read it off the capped list: eighty failures reported "30 failing test(s)". The /tdd loop
    // then fixes thirty, re-runs, finds fifty, and reads them as regressions it just introduced —
    // the exact reasoning error the build-error path paid for in SmartFixValidator. Go is also the
    // one runner with no summary of its own, so that number is all the model gets.

    [Fact]
    public void Go_MoreFailuresThanItLists_CountsThemAll_AndSaysWhatItLeftOut()
    {
        var raw = string.Join("\n", Enumerable.Range(0, 80).Select(i => $"--- FAIL: TestCase{i:00} (0.00s)"));

        var result = RunTestsTool.ParseGoOutput(raw, 1);

        Assert.Contains("80 failing test(s)", result);          // the count is the real one…
        Assert.Contains("+50 more failing test(s)", result);    // …and the list says what it dropped
        Assert.Equal(30, result.Split('\n').Count(l => l.TrimStart().StartsWith("✗ TestCase")));
    }

    [Fact]
    public void Go_FewerFailuresThanItsCap_SaysNothingExtra()
    {
        // Witness: the rule above must read as "the notice appeared", not as "some number
        // appeared". A run that fits adds nothing.
        var raw = "--- FAIL: TestOne (0.00s)\n--- FAIL: TestTwo (0.00s)";

        var result = RunTestsTool.ParseGoOutput(raw, 1);

        Assert.Contains("2 failing test(s)", result);
        Assert.DoesNotContain("more failing test(s) not listed", result);
    }

    [Fact]
    public void Pytest_MoreFailuresThanItLists_SaysWhatItLeftOut()
    {
        // pytest and cargo print their own summary, so the COUNT was always honest here — only the
        // list was silently cut. Same discipline, lesser stakes.
        var raw = "= 80 failed, 1 passed in 2.00s =\n"
                + string.Join("\n", Enumerable.Range(0, 80).Select(i => $"FAILED tests/test_x.py::test_{i:00} - E"));

        var result = RunTestsTool.ParsePytestOutput(raw, 1);

        Assert.Contains("80 failed", result);
        Assert.Contains("+50 more failing test(s)", result);
    }

    [Fact]
    public void Cargo_MoreFailuresThanItLists_SaysWhatItLeftOut()
    {
        var raw = "test result: FAILED. 0 passed; 80 failed; 0 ignored; 0 measured; 0 filtered out\n"
                + string.Join("\n", Enumerable.Range(0, 80).Select(i => $"test case_{i:00} ... FAILED"));

        var result = RunTestsTool.ParseCargoOutput(raw, 1);

        Assert.Contains("Failed: 80", result);
        Assert.Contains("+50 more failing test(s)", result);
    }
}
