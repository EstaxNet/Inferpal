using Inferpal.Services.Commands;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A filter the runner cannot parse runs nothing, and the runner says why — in a line the parsers dropped. go printed
/// "testing: invalid regexp …" and the report was "✗ FAILED — see output" WITHOUT the output: read as red, /tdd patched
/// code for five rounds against a filter. vstest printed "Incorrect format for TestCaseFilter …" and the report blamed
/// renamed tests. The outputs are the runners' real ones (go 1.23, SDK 10 with xUnit v3, captured).
/// </summary>
public class RunnerFilterRejectedTests
{
    [Fact]
    public void Go_AnInvalidRegexp_SaysWhy_AndIsNothingRan()
    {
        var report = RunTestsTool.ParseGoOutput("""
            ?   	example.com/app/cmd	[no test files]
            testing: invalid regexp for element 0 of -test.run ("["): error parsing regexp: missing closing ]: `[`
            FAIL	example.com/app/parser	0.003s
            FAIL
            """, 1);

        Assert.Contains(RunTestsTool.FilterRejected, report);
        Assert.Contains("error parsing regexp: missing closing ]", report);
        Assert.True(TddCommandHandler.NothingRan(report), report);
        Assert.False(TddCommandHandler.TestsPassed(report), report);
    }

    [Fact]
    public void Dotnet_AMalformedFilter_SaysWhy_InsteadOfBlamingRenamedTests()
    {
        var report = RunTestsTool.ParseDotnetOutput("""
            A total of 1 test files matched the specified pattern.
            [xUnit.net 00:00:00.10] SkipX: Exception filtering tests: Incorrect format for TestCaseFilter Error: Missing ')'. Specify the correct format and try again. Note that the incorrect format can lead to no test getting executed.
            No test matches the given testcase filter `(FullyQualifiedName~Broken` in C:\x\SkipX.dll
            """, 0);

        Assert.Contains(RunTestsTool.FilterRejected, report);
        Assert.Contains("Incorrect format for TestCaseFilter Error: Missing ')'", report);
        Assert.DoesNotContain(RunTestsTool.NoTestMatchedFilter, report);
        Assert.True(TddCommandHandler.NothingRan(report), report);
    }

    [Fact]
    public void Jest_AnInvalidPattern_SaysWhy()
    {
        var report = RunTestsTool.ParseNpmOutput("""
            FAIL ./sum.test.js
              ● Test suite failed to run

                SyntaxError: Invalid regular expression: /(/i: Unterminated group
                    at new RegExp (<anonymous>)

            Test Suites: 1 failed, 1 total
            Tests:       0 total
            Snapshots:   0 total
            """, 1, filter: "(");

        Assert.StartsWith(RunTestsTool.FilterRejected, report);
        Assert.Contains("Invalid regular expression: /(/i: Unterminated group", report);
        Assert.True(TddCommandHandler.NothingRan(report), report);
    }

    [Fact]
    public void Jest_ASuiteThatCannotLoad_IsAFailureToFix_NotANoMatch()
    {
        // THE red state of test-first work: the test imports what does not exist yet. "Tests: 0 total" read as "the
        // filter matched nothing", and /tdd stopped on "nothing to fix" — before writing the module.
        var report = RunTestsTool.ParseNpmOutput("""
            FAIL ./cart.test.js
              ● Test suite failed to run

                Cannot find module './cart' from 'cart.test.js'

            Test Suites: 1 failed, 1 total
            Tests:       0 total
            Snapshots:   0 total
            Ran all test suites matching cart.
            """, 1, filter: "cart");

        Assert.StartsWith("✗ FAILED", report);
        Assert.Contains("Cannot find module './cart'", report);
        Assert.False(TddCommandHandler.NothingRan(report), report);
        Assert.False(TddCommandHandler.TestsPassed(report), report);
    }

    [Fact]
    public void Jest_ASuiteThatCannotLoad_NextToOnesThatPass_IsNamed_NotTakenForALinter()
    {
        // The exit code is the unloaded file's, not a gate chained after the tests.
        var report = RunTestsTool.ParseNpmOutput("""
            FAIL ./cart.test.js
              ● Test suite failed to run

                Cannot find module './cart' from 'cart.test.js'

            Test Suites: 1 failed, 1 passed, 2 total
            Tests:       1 skipped, 1 passed, 2 total
            """, 1, filter: "adds");

        Assert.StartsWith("✗ FAILED", report);
        Assert.Contains("1 test file(s) failed to run", report);
        Assert.DoesNotContain("although its test summary passed", report);
    }

    [Fact]
    public void Jest_APatternThatMatchesNothing_StaysNothingRan()
    {
        // Reference arm: every test skipped by the pattern, no suite failed.
        var report = RunTestsTool.ParseNpmOutput("""
            Test Suites: 1 skipped, 0 of 1 total
            Tests:       2 skipped, 2 total
            Snapshots:   0 total
            Ran all test suites with tests matching "nomatch".
            """, 0, filter: "nomatch");

        Assert.StartsWith(RunTestsTool.NoTestMatchedFilter, report);
        Assert.True(TddCommandHandler.NothingRan(report), report);
    }

    [Theory]
    [InlineData("Test Suites: 1 failed, 1 total\nTests:       0 total\n", 1, "✗ FAILED — 1 test file(s) failed to run")]
    [InlineData("Test Suites: 1 skipped, 0 of 1 total\nTests:       2 skipped, 2 total\n", 0, "⚠ No test matched the filter")]
    [InlineData("Tests:       1 failed, 2 passed, 3 total\n", 1, "✗ FAILED — Failed: 1, Passed: 2")]
    public void AJestReportWithWindowsLineEndings_IsReadTheSame(string report, int exitCode, string head)
    {
        // A summary line read up to "$" stopped at the "\r" of a CRLF line and went unread: the Windows CI leg, whose
        // checkout turns the fixtures to CRLF, saw it before anyone's jest did.
        Assert.StartsWith(head, RunTestsTool.ParseNpmOutput(report, exitCode));
        Assert.StartsWith(head, RunTestsTool.ParseNpmOutput(report.Replace("\n", "\r\n"), exitCode));
    }

    [Fact]
    public void AWellFormedFilterThatMatchesNothing_KeepsItsOwnSentence()
    {
        // Reference arm: a valid filter that matches nothing is not a rejected one.
        var dotnet = RunTestsTool.ParseDotnetOutput("No test matches the given testcase filter `Name=Nope` in C:\\x\\SkipX.dll\n", 0);
        var go     = RunTestsTool.ParseGoOutput("testing: warning: no tests to run\nPASS\nok  \texample.com/app/parser\t0.002s [no tests to run]\n", 0);

        Assert.Contains(RunTestsTool.NoTestMatchedFilter, dotnet);
        Assert.DoesNotContain(RunTestsTool.FilterRejected, dotnet);
        Assert.Contains(RunTestsTool.NoTestFound, go);
        Assert.DoesNotContain(RunTestsTool.FilterRejected, go);
    }
}
