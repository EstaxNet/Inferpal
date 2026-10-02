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
        Assert.True(ChatTurnPolicy.LastCheckFailed([Ran("read_file"), Ran("apply_edits"), Ran("run_tests", output)], filesChangedInRun: 1));
        Assert.Contains(Strings.AgentLastCheckFailed, ChatTurnPolicy.EndNotice(false, false, false, lastCheckFailed: true));
    }

    [Fact]
    public void ABuildCheckThatFoundErrors_IsSaid()
    {
        var run = new ChildProcessResult(1, "/ws/src/Cart.cs(9,5): error CS1002: ; expected [/ws/src/Shop.csproj]", "", TimedOut: false);
        var diagnostics = GetDiagnosticsTool.Interpret(run, "Shop.csproj", 90);

        Assert.True(ChatTurnPolicy.LastCheckFailed([Ran("apply_diff"), Ran("get_diagnostics", diagnostics)], filesChangedInRun: 1));
    }

    [Fact]
    public void ReferenceArms_SayNothing()
    {
        // Red, fixed, run again green: the last check passed.
        Assert.False(ChatTurnPolicy.LastCheckFailed([Ran("run_tests", JestFailed), Ran("apply_diff"), Ran("run_tests", Green)], filesChangedInRun: 1));
        // Nothing ran, stopped at the budget, a wrong path: not a failing test — another notice's business, or none.
        Assert.False(ChatTurnPolicy.LastCheckFailed([Ran("run_tests", RunTestsTool.NoTestFound)], filesChangedInRun: 1));
        Assert.False(ChatTurnPolicy.LastCheckFailed([Ran("run_tests", RunTestsTool.StoppedAtBudget + "\n" + JestFailed)], filesChangedInRun: 1));
        Assert.False(ChatTurnPolicy.LastCheckFailed([Ran("run_tests", RunTestsTool.PathNotFound)], filesChangedInRun: 1));
        // No check at all, and a clean build.
        Assert.False(ChatTurnPolicy.LastCheckFailed([Ran("read_file"), Ran("write_file")], filesChangedInRun: 1));
        var clean = GetDiagnosticsTool.Interpret(new ChildProcessResult(0, "", "", TimedOut: false), "Shop.csproj", 90);
        Assert.False(ChatTurnPolicy.LastCheckFailed([Ran("get_diagnostics", clean)], filesChangedInRun: 1));
        // A turn that changed nothing — "does it compile? do not fix anything" — reports the state: the answer that
        // says the build fails is the task done, and a notice would contradict it (measured, Devstral).
        Assert.False(ChatTurnPolicy.LastCheckFailed([Ran("get_diagnostics", RunTestsTool.BuildFailed)], filesChangedInRun: 0));
        Assert.False(ChatTurnPolicy.LastCheckFailed([Ran("run_tests", JestFailed)], filesChangedInRun: 0));
        Assert.False(ChatTurnPolicy.LastCheckFailed([Ran("run_tests", JestFailed)], filesChangedInRun: null));
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

/// <summary>
/// ⚠ The build an edit's Smart Fix note reports is a check like the others: turns that ended on "🔨 Smart Fix: N
/// compilation error(s)" after an edit — the model answering "I have updated the page" over a page that no longer
/// builds — carried no notice (12 battery runs, every one judged failed). The outputs below come from the producer.
/// </summary>
[Collection(CultureSerialCollection.Name)]   // the verdict is read from the producer's localized sentences
public sealed class SmartFixLastCheckTests
{
    private static ToolExecution Ran(string name, string output = "output") => new(name, "{}", output);

    private static string Edit(string? smartFixNote) =>
        Strings.ApplyEditsOk(1, 1) + (smartFixNote is null ? string.Empty : "\n\n" + smartFixNote);

    private static readonly string Red = Edit(Services.CodeActions.SmartFixValidator.Interpret(1,
        "/ws/Components/Pages/Counter.razor(13,7): error RZ1006: The code block is missing a closing \"}\" character.",
        dotnetFilter: true));
    private static readonly string Clean = Edit(Services.CodeActions.SmartFixValidator.Interpret(0, "", dotnetFilter: true));

    [Fact]
    public void ATurnThatEndsOnAnEditWhoseBuildFailed_IsSaid()
    {
        Assert.Equal(true, Services.CodeActions.SmartFixValidator.ReadVerdict(Red));   // witness: the producer's note is read
        Assert.True(ChatTurnPolicy.LastCheckFailed([Ran("read_file"), Ran("apply_edits", Red)], filesChangedInRun: 1));
    }

    [Fact]
    public void ReferenceArms_SayNothing()
    {
        Assert.Equal(false, Services.CodeActions.SmartFixValidator.ReadVerdict(Clean));
        // Red, fixed by the next edit whose build passed.
        Assert.False(ChatTurnPolicy.LastCheckFailed([Ran("apply_edits", Red), Ran("apply_edits", Clean)], filesChangedInRun: 1));
        // Red, then the tests run green.
        Assert.False(ChatTurnPolicy.LastCheckFailed([Ran("apply_edits", Red), Ran("run_tests", "✓ PASSED — Failed: 0, Passed: 3, Skipped: 0, Total: 3")],
                                                    filesChangedInRun: 1));
        Assert.False(ChatTurnPolicy.LastCheckFailed([Ran("apply_edits", Clean)], filesChangedInRun: 1));
        Assert.Null(Services.CodeActions.SmartFixValidator.ReadVerdict(Edit(null)));
    }

    [Fact]
    public void AnEditWithNoBuildNote_DoesNotClearAFailedOne()
    {
        // No note (Smart Fix off for that file, a missing toolchain) is no check: the red build before it stands.
        Assert.True(ChatTurnPolicy.LastCheckFailed([Ran("apply_edits", Red), Ran("write_file", Edit(null))], filesChangedInRun: 1));
    }
}
