using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>run_command</c> with <c>repeat</c> measures a command (pari 103): one card for every run, a warm-up left out, the median
/// and the spread, and against the same command's previous measure whether a difference is beyond the noise. A failing warm-up
/// measures nothing; a background job cannot be measured.
/// </summary>
public sealed class RunCommandRepeatTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("inferpal-repeat-");

    public void Dispose()
    {
        try { _root.Delete(recursive: true); } catch { }
    }

    private sealed class CountingApproval : IApprovalService
    {
        public int Asked;
        public string Details = "";
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null, bool forcePrompt = false)
        {
            Asked++;
            Details = details;
            return Task.FromResult(true);
        }
    }

    private Task<string> Run(RunCommandTool tool, object args) =>
        tool.ExecuteAsync(JsonSerializer.SerializeToElement(args), CancellationToken.None);

    [Fact]
    public async Task AMeasure_AsksOnce_AndGivesTheFigures_ThenComparesTheNextOne()
    {
        var approval = new CountingApproval();
        var tool = new RunCommandTool(approval, new InferpalConfig(), () => _root.FullName);

        var first = await Run(tool, new { command = "echo measured", repeat = 5 });
        var second = await Run(tool, new { command = "echo measured", repeat = 5 });

        Assert.Equal(2, approval.Asked);
        Assert.Contains(Inferpal.Localization.Strings.MeasureRunsOnCard(6), approval.Details);
        Assert.Contains("warm-up run (not counted)", first);
        Assert.Contains("(5 runs)", first);
        Assert.Contains("first measure of this command", first);
        Assert.Contains("measured", first);
        Assert.Contains("against the previous measure of this command", second);
    }

    [Fact]
    public async Task AFailingWarmUp_MeasuresNothing()
    {
        var tool = new RunCommandTool(new CountingApproval(), new InferpalConfig(), () => _root.FullName);

        var said = await Run(tool, new { command = "exit 3", repeat = 5 });

        Assert.Contains("The warm-up run failed, so nothing was measured", said);
        Assert.DoesNotContain("median", said);
    }

    [Fact]
    public async Task ABackgroundJob_CannotBeMeasured_AndNothingRuns()
    {
        var approval = new CountingApproval();
        var tool = new RunCommandTool(approval, new InferpalConfig(), () => _root.FullName);

        var said = await Run(tool, new { command = "echo x", repeat = 5, background = true });

        Assert.Contains("'repeat' measures a command that ends", said);
        Assert.Equal(0, approval.Asked);
    }

    [Fact]
    public async Task ARepeatOutOfBounds_IsClamped_AndSaid()
    {
        var tool = new RunCommandTool(new CountingApproval(), new InferpalConfig(), () => _root.FullName);

        var said = await Run(tool, new { command = "echo x", repeat = 1 });

        Assert.Contains("'repeat' was 1", said);
        Assert.Contains("(5 runs)", said);
    }

    [Fact]
    public async Task ARepeatThatIsNotANumber_IsRefused_AndNothingRuns()
    {
        var approval = new CountingApproval();
        var tool = new RunCommandTool(approval, new InferpalConfig(), () => _root.FullName);

        var said = await Run(tool, new { command = "echo x", repeat = "five" });

        Assert.StartsWith("Error: 'repeat' is a number of runs", said);
        Assert.Equal(0, approval.Asked);
    }

    [Fact]
    public async Task ACdInTheMeasuredCommand_MovesNoRun_AndLeavesTheSessionWhereItWas()
    {
        Directory.CreateDirectory(Path.Combine(_root.FullName, "sub"));
        var tool = new RunCommandTool(new CountingApproval(), new InferpalConfig(), () => _root.FullName);
        await Run(tool, new { command = "cd sub" });
        var before = tool.SessionState.Cwd;

        await Run(tool, new { command = "cd ..", repeat = 5 });

        Assert.Equal(Path.TrimEndingDirectorySeparator(before), Path.TrimEndingDirectorySeparator(tool.SessionState.Cwd));
    }

    [Fact]
    public async Task RunsThatFail_KeepTheMeasureAFailedCheck_WithTheFailedRunsEnd()
    {
        var tool = new RunCommandTool(new CountingApproval(), new InferpalConfig(), () => _root.FullName);
        // The warm-up passes and leaves a file; every counted run then fails with code 4.
        var command = OperatingSystem.IsWindows()
            ? "if (Test-Path ran.txt) { Write-Output 'second time'; exit 4 } else { New-Item ran.txt | Out-Null }"
            : "if [ -f ran.txt ]; then echo 'second time'; exit 4; else touch ran.txt; fi";

        var said = await Run(tool, new { command, repeat = 5 });

        Assert.Contains("no time: all 5 runs failed", said);
        Assert.Contains("[output of run 1, the first that failed]", said);
        Assert.Equal(4, Inferpal.Services.Agent.CheckCommand.ExitCode(said));
    }
}
