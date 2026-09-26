using Inferpal.Services.Commands;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// run_tests returned npm's output raw — the exit code discarded, no verdict line — and /tdd reads green only from a
/// verdict line ("✓ …" or "N passed" on the first line). The raw output starts with "> project@1.0.0 test": on a Node
/// project /tdd never saw green, and ran up to five agent rounds "fixing" a suite that passed, each told it failed.
/// The outputs below are the ones jest, vitest, mocha and node --test print.
/// </summary>
public class NpmTestVerdictTests
{
    private const string JestPass =
        "> demo@1.0.0 test\n> jest\n\nPASS  src/sum.test.js\n  ✓ adds 1 + 2 to equal 3 (2 ms)\n\n" +
        "Test Suites: 1 passed, 1 total\nTests:       1 passed, 1 total\nSnapshots:   0 total\nTime:        0.512 s\nRan all test suites.\n";

    private const string JestFail =
        "> demo@1.0.0 test\n> jest\n\nFAIL  src/sum.test.js\n  ✕ adds 1 + 2 to equal 3 (3 ms)\n\n  ● adds 1 + 2 to equal 3\n\n" +
        "    Expected: 4\n    Received: 3\n\nTest Suites: 1 failed, 1 total\nTests:       1 failed, 2 passed, 3 total\n";

    private const string VitestPass =
        "> demo@1.0.0 test\n> vitest run\n\n ✓ src/sum.test.ts (1 test) 2ms\n\n Test Files  1 passed (1)\n      Tests  1 passed (1)\n   Duration  300ms\n";

    private const string VitestFail =
        "> demo@1.0.0 test\n> vitest run\n\n ❯ src/sum.test.ts (2 tests | 1 failed) 5ms\n   × adds 1 + 2 5ms\n\n" +
        " Test Files  1 failed (1)\n      Tests  1 failed | 1 passed (2)\n";

    private const string MochaPass = "> demo@1.0.0 test\n> mocha\n\n  sum\n    ✓ adds\n\n  1 passing (5ms)\n";

    private const string MochaFail =
        "> demo@1.0.0 test\n> mocha\n\n  sum\n    ✓ adds\n    1) subtracts\n\n  1 passing (6ms)\n  1 failing\n\n  1) sum\n       subtracts:\n     AssertionError\n";

    private const string NodeTestPass = "> demo@1.0.0 test\n> node --test\n\n# tests 3\n# suites 0\n# pass 3\n# fail 0\n";

    // Node 24, output piped: the spec reporter is the default from Node 23 on, terminal or not.
    private const string NodeSpecPass =
        "> demo@1.0.0 test\n> node --test\n\n✔ alpha works (0.4833ms)\nℹ tests 1\nℹ suites 0\nℹ pass 1\nℹ fail 0\n" +
        "ℹ cancelled 0\nℹ skipped 0\nℹ todo 0\nℹ duration_ms 66.8417\n";

    private const string NodeSpecFail =
        "> demo@1.0.0 test\n> node --test\n\n✔ alpha works (0.5723ms)\n✖ beta fails (0.4583ms)\nℹ tests 2\nℹ suites 0\n" +
        "ℹ pass 1\nℹ fail 1\nℹ cancelled 0\nℹ skipped 0\nℹ todo 0\nℹ duration_ms 60.9961\n\n✖ failing tests:\n\n" +
        "test at test\\a.test.js:4:1\n✖ beta fails (0.4583ms)\n  AssertionError [ERR_ASSERTION]: 1 == 2\n";

    private const string NoTestScript =
        "> demo@1.0.0 test\n> echo \"Error: no test specified\" && exit 1\n\n\"Error: no test specified\"\n";

    [Fact]
    public void RawNpmOutput_WasNeverGreen()
    {
        // The defect, measured on the reader: a passing jest run, handed over raw.
        Assert.False(TddCommandHandler.TestsPassed(JestPass));
    }

    [Theory]
    [InlineData(JestPass, 0)]
    [InlineData(VitestPass, 0)]
    [InlineData(MochaPass, 0)]
    [InlineData(NodeTestPass, 0)]
    [InlineData(NodeSpecPass, 0)]
    public void APassingRun_IsGreen(string raw, int exitCode)
    {
        var report = RunTestsTool.ParseNpmOutput(raw, exitCode);

        Assert.True(TddCommandHandler.TestsPassed(report), report);
        Assert.False(TddCommandHandler.NothingRan(report));
    }

    [Theory]
    [InlineData(JestFail, 1, "Failed: 1, Passed: 2")]
    [InlineData(VitestFail, 1, "Failed: 1, Passed: 1")]
    [InlineData(MochaFail, 1, "Failed: 1, Passed: 1")]
    [InlineData(NodeSpecFail, 1, "Failed: 1, Passed: 1")]
    public void AFailingRun_IsRed_WithItsCounts_AndItsDetails(string raw, int exitCode, string counts)
    {
        var report = RunTestsTool.ParseNpmOutput(raw, exitCode);

        Assert.False(TddCommandHandler.TestsPassed(report));
        Assert.Contains(counts, report);
        Assert.Contains(raw.Trim().Split('\n')[^1].Trim(), report);                            // the raw output follows
    }

    [Fact]
    public void AGreenSummaryWithAFailingExit_IsNotGreen()
    {
        // `npm test` can chain a linter or a coverage gate after the tests: the exit code has the last word.
        Assert.False(TddCommandHandler.TestsPassed(RunTestsTool.ParseNpmOutput(JestPass, 1)));
    }

    [Fact]
    public void NpmsDefaultTestScript_RanNothing()
    {
        var report = RunTestsTool.ParseNpmOutput(NoTestScript, 1);

        Assert.True(TddCommandHandler.NothingRan(report), report);
    }

    [Fact]
    public void AnUnreadableSummary_IsNeverGreen()
    {
        // Reference arm, the rule of every other parser: exit 0 without a summary proves nothing.
        var report = RunTestsTool.ParseNpmOutput("> demo@1.0.0 test\n> node run.js\n\nall good\n", 0);

        Assert.False(TddCommandHandler.TestsPassed(report));
        Assert.True(TddCommandHandler.NothingRan(report));
    }
}
