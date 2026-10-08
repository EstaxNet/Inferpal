using Inferpal.Services.Commands;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A test run that exits on a failure has a red verdict, whether or not it printed a test summary.
//
//  The parsers built their verdict from the runner's summary line and, without one, handed back the
//  raw output: a crate that did not compile, a "tsc && jest" whose compilation failed, a test setup
//  that crashed before the runner started. No reader saw a failure in that text — no end-of-turn
//  notice, no failed check on the run's result bar. Go's parser already said "✗ FAILED — see output".
//  The cargo and npm outputs below are real (cargo 1.x --quiet in WSL; npm 11 under Node 24), paths
//  shortened.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class FailingRunWithoutSummaryTests
{
    private const string CargoDidNotCompile = """
        error[E0425]: cannot find value `totl` in this scope
         --> src/lib.rs:2:5
          |
        2 |     totl
          |     ^^^^ not found in this scope

        For more information about this error, try `rustc --explain E0425`.
        error: could not compile `shop` (lib) due to 1 previous error
        error: could not compile `shop` (lib test) due to 1 previous error
        """;

    private const string NpmCompileStepFailed = """

        > shop@1.0.0 test
        > node build.js && node --test

        src/cart.ts(3,5): error TS2304: Cannot find name 'totl'.
        """;

    private const string NpmSetupCrashed = """

        > shop@1.0.0 test
        > node build.js && node --test

        C:\ws\build.js:1
        throw new Error("cannot connect to the test database");
        ^

        Error: cannot connect to the test database
            at Object.<anonymous> (C:\ws\build.js:1:7)

        Node.js v24.18.0
        """;

    [Fact]
    public void ACrateThatDidNotCompile_IsABuildFailure_WithItsErrors()
    {
        var report = RunTestsTool.ParseCargoOutput(CargoDidNotCompile, exitCode: 101);

        Assert.StartsWith(RunTestsTool.BuildFailed, report);
        Assert.Contains("error[E0425]: cannot find value `totl` in this scope (src/lib.rs:2:5)", report);
        Assert.DoesNotContain("could not compile", report);
        Assert.True(TddCommandHandler.TestsFailed(report));
    }

    [Fact]
    public void AnNpmTestWhoseCompileStepFailed_IsABuildFailure()
    {
        var report = RunTestsTool.ParseNpmOutput(NpmCompileStepFailed, exitCode: 2);

        Assert.StartsWith(RunTestsTool.BuildFailed, report);
        Assert.Contains("error TS2304: Cannot find name 'totl'.", report);
        Assert.True(TddCommandHandler.TestsFailed(report));
    }

    [Fact]
    public void AnNpmTestThatCrashedBeforeItsRunner_IsRed_WithItsError()
    {
        var report = RunTestsTool.ParseNpmOutput(NpmSetupCrashed, exitCode: 1);

        Assert.StartsWith(RunTestsTool.NoSummaryFailure("npm test", 1), report);
        Assert.Contains("cannot connect to the test database", report);
        Assert.True(TddCommandHandler.TestsFailed(report));
    }

    // Every parser run_tests has. ⚠ InlineData, not MemberData: DocCountersTests counts theory cases from InlineData.
    private static string Parse(string runner, string raw, int exitCode) => runner switch
    {
        "dotnet" => RunTestsTool.ParseDotnetOutput(raw, exitCode),
        "pytest" => RunTestsTool.ParsePytestOutput(raw, exitCode),
        "npm"    => RunTestsTool.ParseNpmOutput(raw, exitCode),
        "cargo"  => RunTestsTool.ParseCargoOutput(raw, exitCode),
        "go"     => RunTestsTool.ParseGoOutput(raw, exitCode),
        _        => throw new ArgumentOutOfRangeException(nameof(runner), runner, null),
    };

    [Theory]
    [InlineData("dotnet")] [InlineData("pytest")] [InlineData("npm")] [InlineData("cargo")] [InlineData("go")]
    public void EveryRunner_AFailingExitCodeWithoutASummary_IsRed(string runner)
    {
        var report = Parse(runner, "Segmentation fault (core dumped)", 3);

        Assert.True(TddCommandHandler.TestsFailed(report), $"{runner}: {report}");
    }

    [Theory]
    [InlineData("dotnet")] [InlineData("pytest")] [InlineData("npm")] [InlineData("cargo")] [InlineData("go")]
    public void EveryRunner_ACleanExitWithoutASummary_ProvesNothing(string runner)
    {
        // Reference arm: exit 0 without a summary stays "nothing ran"; and a run without an exit code (killed at its
        // budget, which says so above the report) gets no verdict of its own.
        Assert.True(TddCommandHandler.NothingRan(Parse(runner, "done.", 0)), runner);
        Assert.False(TddCommandHandler.TestsFailed(Parse(runner, "partial output", -1)), runner);
    }

    [Theory]
    [InlineData("dotnet")] [InlineData("pytest")] [InlineData("npm")] [InlineData("cargo")] [InlineData("go")]
    public void EveryRunner_KeepsTheWordsOfARunnerThatDidNotStart(string runner)
    {
        // go's parser answered "✗ FAILED — see output" WITHOUT the output: /tdd never read that go was not installed.
        var report = Parse(runner, $"{RunTestsTool.RunnerNotStarted} '{runner}': The system cannot find the file specified.", -1);

        Assert.True(TddCommandHandler.NothingRan(report), $"{runner}: {report}");
    }
}
