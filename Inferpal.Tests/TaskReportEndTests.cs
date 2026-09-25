using System.IO;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Inference;
using Inferpal.Services.Tasks;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ A background task's report is read later, out of the conversation — and it was the run's final
/// answer, nothing more. A run stopped because the model kept repeating itself, or whose answer stopped
/// at the length limit, reported as a finished investigation: the rule that the chat now follows ("a
/// line after the answer says it is not a task carried to its end") had a third reader that did not.
/// </summary>
[Collection(CultureSerialCollection.Name)]   // compares localized sentences
public class TaskReportEndTests
{
    private static AgentResult Run(string answer, bool loop = false, bool cut = false) =>
        new(answer, [], [], WasLoopDetected: loop, AnswerCut: cut);

    [Fact]
    public void ACutReport_SaysItIsIncomplete_AfterTheReport()
    {
        var report = BackgroundTaskQueue.TaskRunOutcome.Of(Run("The cache is invalidated in", cut: true), []).Report;

        Assert.StartsWith("The cache is invalidated in", report);            // the answer stays
        Assert.EndsWith(Strings.AnswerCutAtLimit, report);                    // and the line follows it
    }

    [Fact]
    public void AReportOfALoopingRun_SaysSo()
    {
        var report = BackgroundTaskQueue.TaskRunOutcome.Of(Run("Here is what was gathered.", loop: true), []).Report;

        Assert.EndsWith(Strings.AgentEndedOnRepeat, report);
    }

    /// <summary>
    /// A run that FAILED — backend down, request refused — returns its error as the answer (the agent loop never
    /// throws), and the queue marks a task failed only when its runner throws. The task finished as a success whose
    /// report was the error message: "✅ finished" for an investigation that never happened.
    /// </summary>
    [Fact]
    public async Task AFailedRun_FailsTheTask_WithItsCause()
    {
        using var queue = new BackgroundTaskQueue((_, _, _) => Task.FromResult(
            BackgroundTaskQueue.TaskRunOutcome.Of(new AgentResult("the backend refused the request", [], [], Failed: true), [])));

        var id = queue.Submit("audit the cache")!;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (queue.Get(id)?.IsFinished != true && DateTime.UtcNow < deadline) await Task.Delay(10);

        var task = queue.Get(id)!;
        Assert.True(task.IsFinished, "the task never finished");                      // witness
        Assert.Equal(BackgroundTaskState.Failed, task.State);
        Assert.Contains("the backend refused the request", task.Error);
    }

    [Fact]
    public void AFinishedReport_IsTheAnswer_WithNothingAdded()
    {
        // Reference arm: a note under every report is the noise that gets it ignored.
        Assert.Equal("Done.", BackgroundTaskQueue.TaskRunOutcome.Of(Run("Done."), []).Report);
    }

    [Theory]
    [InlineData("Inferpal", "ToolWindow", "InferpalToolWindowData.SlashCommands.cs")]
    [InlineData("Inferpal.Host", "HostSlashCommands.cs")]
    public void BothTaskRunners_BuildTheirOutcomeFromTheRun(params string[] parts)
    {
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(), Path.Combine(parts)));

        Assert.Contains("new BackgroundTaskToolRegistry(", code, StringComparison.Ordinal);   // WITNESS: the task runner
        Assert.Contains("TaskRunOutcome.Of(run,", code, StringComparison.Ordinal);
        Assert.DoesNotContain("run.FinalResponse, recorder", code, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
