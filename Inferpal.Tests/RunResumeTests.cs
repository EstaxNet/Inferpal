using System.IO;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What a run carries to be resumed after the closing cut it is kept in the run's file as the run goes — never at its end,
/// which an editor killed mid-run never reaches — and read back by the next process.
/// </summary>
public sealed class RunResumeTests : IDisposable
{
    private readonly string _dir  = Directory.CreateTempSubdirectory("inferpal-resume").FullName;
    private readonly string _user = Directory.CreateTempSubdirectory("inferpal-resume-key").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* cleanup */ }
        try { Directory.Delete(_user, recursive: true); } catch { /* cleanup */ }
    }

    private FileHistoryService Service() => new() { WorkspaceRoot = () => _dir, UserHistoryRoot = _user };

    private async Task Edit(FileHistoryService history, string name, string content)
    {
        var path = Path.Combine(_dir, name);
        if (!File.Exists(path)) await File.WriteAllTextAsync(path, "before\n");
        var (saved, _) = await history.BackUpBeforeChangeAsync(path, CancellationToken.None);
        Assert.True(saved);
        await File.WriteAllTextAsync(path, content);
    }

    [Fact]
    public async Task ARunCutMidPlan_IsReadBackWithItsQuestion_Plan_Steps_AndLastCheck()
    {
        var history = Service();
        history.BeginRun();
        history.NoteResumeStart("Apply the discount before the tax, with a test", "agent");
        history.NoteResume(r => r.WithPlan("Discount before tax",
            [new("Read the cart total", true), new("Move the discount", true), new("Write the test", false)]));
        await Edit(history, "total.ts", "after\n");
        history.NoteResume(r => r.WithStep("edit_file src/cart/total.ts"));
        history.NoteResume(r => r.WithCheck(passed: false, "tests failed, 2 of 14"));
        // No EndRun: the editor was killed here, so only what was written as the run went is on disk.

        var later = Service();
        var run = Assert.Single(later.Runs);
        var resume = run.Resume!;
        Assert.Equal(("Apply the discount before the tax, with a test", "agent", "Discount before tax"),
                     (resume.Question, resume.Mode, resume.Goal));
        Assert.Equal([new ResumeStep("Read the cart total", true), new("Move the discount", true), new("Write the test", false)], resume.Plan);
        Assert.Equal(["edit_file src/cart/total.ts"], resume.Steps);
        Assert.Equal(new ResumeCheck(false, "tests failed, 2 of 14"), resume.Check);
        Assert.Equal(2, resume.DoneSteps);
    }

    [Fact]
    public async Task ARunStartedWithoutAQuestion_CarriesNothingToResume()
    {
        // REFERENCE ARM: a slash command's run (no question noted) has nothing to resume, and says nothing of it.
        var history = Service();
        history.BeginRun();
        history.NoteResume(r => r.WithStep("never kept"));
        await Edit(history, "a.cs", "x\n");
        history.EndRun();
        Assert.Null(Assert.Single(Service().Runs).Resume);
    }

    [Fact]
    public void TheActionLines_KeepTheLatest_AndEveryTextIsBounded()
    {
        var resume = RunResume.Start(new string('q', RunResume.MaxQuestion + 10), "chat");
        for (var i = 0; i < RunResume.MaxSteps + 5; i++) resume = resume.WithStep($"step {i} " + new string('x', RunResume.MaxLine));
        Assert.Equal(RunResume.MaxSteps, resume.Steps.Count);
        Assert.StartsWith("step 5 ", resume.Steps[0]);
        Assert.All(resume.Steps, s => Assert.Equal(RunResume.MaxLine + 1, s.Length));
        Assert.Equal(RunResume.MaxQuestion + 1, resume.Question.Length);
    }

    [Fact]
    public async Task TheRecorder_KeepsThePlanAsItGoes_EachAction_AndTheLastCheckThatProved()
    {
        var history = Service();
        history.BeginRun();
        var recorder = Services.Agent.RunResumeRecorder.Start(history, "Fix the total", agent: true);
        var plan = new Models.AgentPlan
        {
            Goal = "Fix",
            Steps = [new() { Index = 1, Description = "Read" }, new() { Index = 2, Description = "Edit" }, new() { Index = 3, Description = "Test" }],
        };
        var shown = new List<string>();
        recorder.OnPlan(_ => shown.Add("plan"))(plan);
        plan.Steps[0].Status = Models.AgentStepStatus.Done;
        recorder.OnStep((_, s) => shown.Add($"{s}"))(0, Models.AgentStepStatus.Done);
        // A skipped step is past: resuming never sends the model back to it.
        plan.Steps[1].Status = Models.AgentStepStatus.Skipped;
        recorder.OnStep(null)(1, Models.AgentStepStatus.Skipped);
        await Edit(history, "total.ts", "after\n");
        recorder.OnTool(e => shown.Add(e.Name))(new ToolExecution("write_file", """{"path":"src/total.ts","content":"x"}""", "ok"));
        recorder.OnTool(null)(new ToolExecution("run_tests", "{}", "✗ FAILED — Failed: 1, Passed: 2, Skipped: 0, Total: 3"));

        // The front-end's own callbacks still run, in order.
        Assert.Equal(["plan", "Done", "write_file"], shown);
        var resume = Assert.Single(Service().Runs).Resume!;
        Assert.Equal("agent", resume.Mode);
        Assert.Equal([true, true, false], resume.Plan.Select(s => s.Done));
        Assert.Equal(["write_file src/total.ts", "run_tests"], resume.Steps);
        Assert.Equal(new ResumeCheck(false, "tests"), resume.Check);
    }

    [Theory]
    [InlineData("""{"command":"dotnet test\nx"}""", false, "run_command dotnet test x")]
    [InlineData("""not json""", false, "run_command")]
    [InlineData("""{"path":"a.cs"}""", true, "run_command a.cs (failed)")]
    public void AnAction_IsOneLine_ItsToolAndWhatItActedOn(string input, bool failed, string line) =>
        Assert.Equal(line, Services.Agent.RunResumeRecorder.Line(new ToolExecution("run_command", input, "", HasErrors: failed)));

    [Fact]
    public async Task OnlyTheCurrentRun_IsNoted_NeverOneAlreadyClosed()
    {
        var history = Service();
        history.BeginRun();
        history.NoteResumeStart("first", "chat");
        await Edit(history, "a.cs", "x\n");
        history.EndRun();
        history.NoteResume(r => r.WithStep("after the run"));
        Assert.Empty(Assert.Single(Service().Runs).Resume!.Steps);
    }
}
