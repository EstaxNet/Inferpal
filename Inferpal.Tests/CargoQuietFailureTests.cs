using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A failing cargo test is named, with what its assertion compared.
//
//  run_tests runs `cargo test --quiet`, and today's cargo prints a failing test there as "tests::adds --- FAILED",
//  not as the verbose "test tests::adds ... FAILED" the parser read: the report was "✗ FAILED — Failed: 1" with no
//  name and no message, though cargo printed both.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class CargoQuietFailureTests
{
    // Verbatim, `cargo test --quiet` on a crate with one failing test (cargo 1.9x, WSL Ubuntu).
    private const string Quiet = """
        running 2 tests
        tests::adds --- FAILED
        .
        failures:

        ---- tests::adds stdout ----

        thread 'tests::adds' (113509) panicked at src/lib.rs:5:25:
        assertion `left == right` failed
          left: 0
         right: 4
        note: run with `RUST_BACKTRACE=1` environment variable to display a backtrace


        failures:
            tests::adds

        test result: FAILED. 1 passed; 1 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.00s

        error: test failed, to rerun pass `--lib`
        """;

    [Fact]
    public void AQuietRun_NamesTheFailingTest_AndItsAssertion()
    {
        var report = RunTestsTool.ParseCargoOutput(Quiet, 101);

        Assert.StartsWith("✗ FAILED — Failed: 1, Passed: 1", report);
        Assert.Contains("✗ tests::adds", report);
        Assert.Contains("panicked at src/lib.rs:5:25", report);
        Assert.Contains("left: 0", report);
        Assert.Contains("right: 4", report);
        Assert.DoesNotContain("RUST_BACKTRACE", report);
        Assert.Single(RunTestsTool.CargoFailingTests(Quiet));                 // the three shapes name it once
    }

    [Fact]
    public void TheClosingList_AloneIsEnough()
    {
        // The one form every cargo version prints: no per-test line at all, the names under "failures:".
        var raw = "failures:\n    a::one\n    a::two\n\ntest result: FAILED. 0 passed; 2 failed; 0 ignored; 0 measured; 0 filtered out\n";

        Assert.Equal(["a::one", "a::two"], RunTestsTool.CargoFailingTests(raw));
    }

    [Fact]
    public void ARunCutBeforeItsClosingList_StillNamesWhatFailed()
    {
        // A run killed at its budget never reaches "failures:": the quiet per-test line is all there is.
        var raw = "running 40 tests\ntests::adds --- FAILED\n..........\n";

        Assert.Equal(["tests::adds"], RunTestsTool.CargoFailingTests(raw));
    }

    [Fact]
    public void APassingQuietRun_ListsNothing()
    {
        // Reference arm: green, nothing to name.
        var raw = "running 2 tests\n..\ntest result: ok. 2 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.00s\n";

        var report = RunTestsTool.ParseCargoOutput(raw, 0);

        Assert.StartsWith("✓ PASSED", report);
        Assert.DoesNotContain("Failing tests:", report);
    }
}
