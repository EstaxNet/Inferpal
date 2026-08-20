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

        Assert.Contains("✓ Tests passed.", result);
        Assert.DoesNotContain("Failing tests:", result);
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
}
