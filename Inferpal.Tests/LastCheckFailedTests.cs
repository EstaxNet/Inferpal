using System.IO;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Agent;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A turn whose last test or build check failed says so after the answer.
//
//  Measured in the model battery: gpt-oss answered "renamed across the entire solution … all tests
//  now pass" right after run_tests said "✗ BUILD FAILED"; Llama 3.1 ("the bug has been fixed") and
//  Qwen3 Coder ("renamed, including the test file") ended the same way on a failing test. What the
//  answer claims is wording; whether the last check passed is a fact of the run, read from the
//  tools' own verdicts. The reference arms keep the notice for a real failure: a red run fixed and
//  run again green, a run where nothing ran, a run stopped at its budget, a wrong path — silence.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class LastCheckFailedTests
{
    private static ToolExecution Ran(string name, string output = "output") => new(name, "{}", output);

    // The three outputs of the battery, verbatim heads.
    private const string BuildFailed = RunTestsTool.BuildFailed
        + "\n  /ws/tests/Shop.Tests/CartTests.cs(12,31): error CS1061: 'Cart' does not contain a definition for 'ComputeTotal'";
    private const string JestFailed = "✗ FAILED — Failed: 1, Passed: 2, Skipped: 0, Total: 3\n\n> test\n> jest\nPASS tests/cart.test.js\nFAIL tests/pricing.test.js";
    private const string PytestFailed = "1 failed, 2 passed in 0.01s\n\nFailing tests:\n  FAILED tests/test_cart.py::test_compute_total_sums_the_prices";
    private const string Green = "✓ PASSED — Failed: 0, Passed: 3, Skipped: 0, Total: 3";

    [Theory]
    [InlineData(BuildFailed)]
    [InlineData(JestFailed)]
    [InlineData(PytestFailed)]
    public void AFailingLastTestRun_IsSaid(string output)
    {
        Assert.True(ChatTurnPolicy.LastCheckFailed([Ran("read_file"), Ran("apply_edits"), Ran("run_tests", output)]));
        Assert.Contains(Strings.AgentLastCheckFailed, ChatTurnPolicy.EndNotice(false, false, false, lastCheckFailed: true));
    }

    [Fact]
    public void ABuildCheckThatFoundErrors_IsSaid()
    {
        var run = new ChildProcessResult(1, "/ws/src/Cart.cs(9,5): error CS1002: ; expected [/ws/src/Shop.csproj]", "", TimedOut: false);
        var diagnostics = GetDiagnosticsTool.Interpret(run, "Shop.csproj", 90);

        Assert.True(ChatTurnPolicy.LastCheckFailed([Ran("apply_diff"), Ran("get_diagnostics", diagnostics)]));
    }

    [Fact]
    public void ReferenceArms_SayNothing()
    {
        // Red, fixed, run again green: the last check passed.
        Assert.False(ChatTurnPolicy.LastCheckFailed([Ran("run_tests", JestFailed), Ran("apply_diff"), Ran("run_tests", Green)]));
        // Nothing ran, stopped at the budget, a wrong path: not a failing test — another notice's business, or none.
        Assert.False(ChatTurnPolicy.LastCheckFailed([Ran("run_tests", RunTestsTool.NoTestFound)]));
        Assert.False(ChatTurnPolicy.LastCheckFailed([Ran("run_tests", RunTestsTool.StoppedAtBudget + "\n" + JestFailed)]));
        Assert.False(ChatTurnPolicy.LastCheckFailed([Ran("run_tests", RunTestsTool.PathNotFound)]));
        // No check at all, and a clean build.
        Assert.False(ChatTurnPolicy.LastCheckFailed([Ran("read_file"), Ran("write_file")]));
        var clean = GetDiagnosticsTool.Interpret(new ChildProcessResult(0, "", "", TimedOut: false), "Shop.csproj", 90);
        Assert.False(ChatTurnPolicy.LastCheckFailed([Ran("get_diagnostics", clean)]));
        Assert.Equal(string.Empty, ChatTurnPolicy.EndNotice(false, false, false));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Inferpal.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    [Theory]
    [InlineData("Inferpal/ToolWindow/InferpalToolWindowData.ChatTurn.cs")]
    [InlineData("Inferpal.Host/HostServer.cs")]
    public void BothFrontEnds_AskOnTheOrchestratedAndTheBasicLoop(string relative)
    {
        // Neither call site runs from the suite (Remote UI on one side, a live session on the other): read the code.
        var code = ConventionCoverageTests.CodeOnly(Path.Combine([RepoRoot(), .. relative.Split('/')]));

        Assert.Contains("ChatTurnPolicy.EndNotice(", code, StringComparison.Ordinal);   // witness
        Assert.Equal(2, code.Split("ChatTurnPolicy.LastCheckFailed(").Length - 1);
    }
}
