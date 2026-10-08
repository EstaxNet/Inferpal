using Inferpal.Localization;
using Inferpal.Services.Commands;
using Inferpal.Services.Tasks;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The step journal of a background task keeps its last <see cref="BackgroundTaskQueue.MaxSteps"/> entries. The report
/// counts the steps the task TOOK, and names how many earlier ones were not kept — a title that counts the kept ones
/// reads "200 steps" for a run of 600. Expectations are literals.
/// </summary>
[Collection(CultureSerialCollection.Name)]
public class TaskStepCountTests
{
    private static async Task<string> ReportOf(int steps, string language)
    {
        using var queue = new BackgroundTaskQueue((_, onStep, _) =>
        {
            for (var i = 1; i <= steps; i++) onStep($"step {i}");
            return Task.FromResult(BackgroundTaskQueue.TaskRunOutcome.Of("done"));
        });
        var id = queue.Submit("audit")!;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (queue.Get(id)?.IsFinished != true)
        {
            Assert.True(DateTime.UtcNow < deadline, "the task did not finish");
            await Task.Delay(10);
        }
        try
        {
            Strings.ApplyLanguage(language);
            return TaskCommandHandler.RenderReport(queue.Get(id)!);
        }
        finally { Strings.ApplyLanguage(null); }
    }

    [Fact]
    public async Task ALongRun_CountsEveryStepItTook_AndNamesTheOnesNotKept()
    {
        var report = await ReportOf(600, "en");

        Assert.Contains("<summary>Steps (600 — the last 200 kept)</summary>", report);
        Assert.Contains("- … 400 earlier steps not kept", report);
        Assert.Contains("- step 600", report);
        Assert.Contains("- step 401", report);
        Assert.DoesNotContain("- step 400\n", report.Replace("\r\n", "\n"));
        // The cut is said once, in the reader's language.
        Assert.DoesNotContain("earlier steps dropped", report);
    }

    [Fact]
    public async Task ARunThatFits_SaysItsCount_AndNoCut()
    {
        var report = await ReportOf(3, "en");

        Assert.Contains("<summary>Steps (3)</summary>", report);
        Assert.DoesNotContain("not kept", report);
    }

    [Fact]
    public async Task OneStepPastTheCap_IsOneStepNotKept()
    {
        var report = await ReportOf(BackgroundTaskQueue.MaxSteps + 1, "en");

        Assert.Contains("- … 1 earlier step not kept", report);
        Assert.Contains("- … 401 étapes antérieures non gardées", await ReportOf(601, "fr"));
    }
}
