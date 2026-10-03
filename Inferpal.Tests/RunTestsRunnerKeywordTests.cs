using System.IO;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A <c>runner</c> the model names and <c>run_tests</c> does not know fell into the default branch: "No test runner
/// detected. Provide 'path'… or set 'runner' explicitly" — the remedy the model had just applied, read as "this project
/// has no tests". The names a model reaches for first are the JavaScript frameworks, which the filter's own
/// description names: they run through npm. Anything else is refused by name.
/// </summary>
public sealed class RunTestsRunnerKeywordTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"runner-{Guid.NewGuid():N}");

    public RunTestsRunnerKeywordTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private async Task<string> RunAsync(object args)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(args));
        return await new RunTestsTool(() => _dir).ExecuteAsync(doc.RootElement, CancellationToken.None);
    }

    [Theory]
    [InlineData("xunit")]
    [InlineData("python")]
    public async Task AnUnknownRunner_IsNamed_NotAnsweredWithNoRunnerDetected(string runner)
    {
        var report = await RunAsync(new { runner });

        Assert.StartsWith($"Unknown runner '{runner}'. Use one of: auto, dotnet, pytest, npm, cargo, go", report);
        Assert.DoesNotContain("No test runner detected", report);
    }

    [Theory]
    [InlineData("jest", "npm")]
    [InlineData("vitest", "npm")]
    [InlineData("mocha", "npm")]
    [InlineData("node", "npm")]
    [InlineData("dotnet", "dotnet")]
    [InlineData("go", "go")]
    public void TheJavaScriptFrameworks_RunThroughNpm(string forced, string runner) =>
        Assert.Equal(runner, RunTestsTool.ForcedRunner(forced));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task NoLimitAsked_TakesTheMaximum_AndSaysSo_InsteadOfKillingTheRunAtOnce(int asked)
    {
        // An empty folder: the run itself has nothing to do, so what is measured is only what the budget became.
        var report = await RunAsync(new { timeout_seconds = asked });

        Assert.StartsWith($"Note: 'timeout_seconds' was {asked}; there is no unlimited run, so this one uses the maximum, 1800.", report);
        Assert.DoesNotContain(RunTestsTool.StoppedAtBudget, report);
    }

    [Fact]
    public async Task ABudgetBeyondTheMaximum_IsClamped_AndSaid()
    {
        var report = await RunAsync(new { timeout_seconds = 99_999 });

        Assert.StartsWith("Note: 'timeout_seconds' was 99999; this tool accepts 1-1800", report);
    }

    [Fact]
    public async Task NoRunnerAndNothingToDetect_StillSaysSo()
    {
        var report = await RunAsync(new { runner = "auto" });

        Assert.StartsWith("No test runner detected", report);
    }
}
