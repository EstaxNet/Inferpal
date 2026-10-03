using System.Text;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>run_tests</c> hands the model the runner's raw output beside its verdict, capped — and the cap kept the HEAD,
/// while every runner prints its failures and its summary LAST. A chatty run reached the model as thousands of
/// passing lines under "✗ FAILED … the error is below", and the error was not below.
/// </summary>
public class RunTestsRawEndTests
{
    /// <summary>A jest run: the npm banner, many passing suites, then the failure and the summary.</summary>
    private static string JestRun()
    {
        var sb = new StringBuilder("> project@1.0.0 test\n> jest\n\n");
        for (var i = 0; i < 400; i++) sb.Append($"PASS src/feature{i}.test.js\n");
        sb.Append("FAIL src/sum.test.js\n  ● sum › adds two numbers\n\n    Expected: 3\n    Received: 4\n\n");
        sb.Append("Test Suites: 1 failed, 400 passed, 401 total\nTests:       1 failed, 400 passed, 401 total\n");
        return sb.ToString();
    }

    [Fact]
    public void ALongFailingRun_ShowsItsFailure_NotOnlyItsPassingLines()
    {
        var result = RunTestsTool.ParseNpmOutput(JestRun(), exitCode: 1);

        Assert.StartsWith("✗ FAILED", result);                                                     // witness: parsed
        Assert.Contains("Expected: 3", result);
        Assert.Contains("Received: 4", result);
        Assert.Contains("> project@1.0.0 test", result);                                           // what ran
        Assert.Contains("characters cut here", result);
    }

    [Fact]
    public void AShortRun_IsShownWhole()
    {
        var raw = "> project@1.0.0 test\n> jest\n\nPASS src/a.test.js\nTests:       1 passed, 1 total\n";

        var result = RunTestsTool.ParseNpmOutput(raw, exitCode: 0);

        Assert.Contains("PASS src/a.test.js", result);
        Assert.DoesNotContain("cut here", result);
    }

    [Fact]
    public void TheCut_KeepsWholeLines_AndStaysWithinTheBudget()
    {
        var raw = string.Concat(Enumerable.Range(0, 2000).Select(i => $"line {i:D4} of the output\n"));

        var kept = RunTestsTool.Truncate(raw, 6000);

        Assert.True(kept.Length <= 6000 + 120, $"kept {kept.Length} characters");
        Assert.Contains("line 1999 of the output", kept);
        Assert.Contains("line 0000 of the output", kept);
        foreach (var line in kept.Split('\n').Where(l => l.StartsWith("line", StringComparison.Ordinal)))
            Assert.Matches(@"^line \d{4} of the output$", line);
    }
}
