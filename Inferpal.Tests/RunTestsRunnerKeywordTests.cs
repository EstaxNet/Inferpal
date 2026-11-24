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

        Assert.StartsWith($"Error: Unknown runner '{runner}'. Use one of: auto, dotnet, pytest, npm, cargo, go", report);
        Assert.True(Inferpal.Services.Execution.ToolFailure.IsFailure(report));   // a refused call, never a run
        Assert.DoesNotContain("No test runner detected", report);
    }

    /// <summary>⚠ A cargo filter that starts with '-' is one of cargo's options, and <c>--config</c> can name a program
    /// cargo runs: refused before anything starts — this tool asks no approval.</summary>
    [Fact]
    public async Task ACargoFilterThatReadsAsAnOption_IsRefused_BeforeCargoStarts()
    {
        File.WriteAllText(Path.Combine(_dir, "Cargo.toml"), "[package]\nname = \"x\"\nversion = \"0.1.0\"\n");

        var report = await RunAsync(new { runner = "cargo", filter = "--config=target.'cfg(all())'.runner=\"calc\"" });

        Assert.Contains(RunTestsTool.FilterRejected, report);
        Assert.Contains("cannot start with '-'", report);
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

    [Theory]
    [InlineData("go.mod", "go")]
    [InlineData("Cargo.toml", "cargo")]
    public void AModuleFileAboveTheWorkspace_ChoosesNoRunner_NothingRunsOutsideIt(string marker, string runner)
    {
        // The person agreed to run the workspace's code: a go.mod in a folder above it is another project, and its
        // tests would run with their working folder outside the workspace.
        var workspace = Path.Combine(_dir, "tool");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(_dir, marker), "");
        Assert.Equal("unknown", RunTestsTool.DetectRunner(workspace, null, workspace));

        // Reference arm: the same file in the workspace, or above the asked path but still inside it, is its project.
        var nested = Path.Combine(workspace, "pkg");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(workspace, marker), "");
        Assert.Equal(runner, RunTestsTool.DetectRunner(nested, nested, workspace));
    }

    [Theory]
    [InlineData("tests/Lib.Tests.fsproj")]
    [InlineData("tests/Lib.Tests.vbproj")]
    [InlineData("tests/Lib.Tests.csproj")]   // reference arm
    public void AnFSharpOrVisualBasicProject_IsRunByDotnet_AsACSharpOneIs(string project)
    {
        // dotnet test runs these projects: named by path, or the only kind of project in the folder.
        Assert.Equal("dotnet", RunTestsTool.RunnerOfPath(project));
        var file = Path.Combine(_dir, project.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Assert.Equal("dotnet", RunTestsTool.DetectRunner(_dir, null, _dir));
    }
}
