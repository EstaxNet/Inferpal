using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// Output parsing for the cargo (Rust) and go test runners added alongside the polyglot Smart Fix.
public class RunTestsParsersTests
{
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
