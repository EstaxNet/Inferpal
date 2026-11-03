using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A background task outlives the process that ran it (<see cref="TaskJournal"/>): its report and proposals come back at
/// the next start, a task the closing stopped is said as such, a number is never given twice, and a queue whose server
/// does not answer waits for it instead of failing its tasks one after another.
/// </summary>
public sealed class TaskJournalTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);   // waits for something to happen, measures nothing
    private readonly string _dir  = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"task-journal-{Guid.NewGuid():N}");
    private string Root => Path.Combine(_dir, "workspace");
    private string File => Path.Combine(_dir, "tasks", "workspace-vs.json");
    private readonly List<BackgroundTaskQueue> _queues = [];

    public void Dispose()
    {
        foreach (var q in _queues) q.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private BackgroundTaskQueue Queue(BackgroundTaskQueue.TaskRunner runner,
                                      Func<CancellationToken, Task<string?>>? serverDown = null, Func<string>? root = null)
    {
        var q = new BackgroundTaskQueue(runner, waitForChatIdle: _ => Task.CompletedTask, currentRoot: root ?? (() => Root),
                                        journalFor: root => root is null ? null : new TaskJournal(File),
                                        serverDown: serverDown, delay: (_, ct) => Task.Delay(10, ct));
        _queues.Add(q);
        return q;
    }

    private static Task<BackgroundTaskQueue.TaskRunOutcome> Report(string text) =>
        Task.FromResult(BackgroundTaskQueue.TaskRunOutcome.Of(text));

    /// <summary>A runner that never ends on its own: it holds its task until it is cancelled.</summary>
    private static async Task<BackgroundTaskQueue.TaskRunOutcome> Forever(BackgroundTaskSnapshot task, Action<string> onStep, CancellationToken ct)
    {
        onStep("reading");
        await Task.Delay(System.Threading.Timeout.Infinite, ct);
        return BackgroundTaskQueue.TaskRunOutcome.Of("never");
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail($"Timed out waiting for: {what}");
    }

    [Fact]
    public async Task AFinishedTask_ComesBackAtTheNextStart_AndItsNumberIsNeverGivenAgain()
    {
        var first = Queue((_, _, _) => Report("THE REPORT"));
        Assert.Equal("t1", first.Submit("find the callers"));
        await WaitUntil(() => first.Get("t1")?.IsFinished == true, "t1 finishes");
        first.Dispose();

        var next = Queue((_, _, _) => Report("later"));
        var kept = next.Get("t1");
        Assert.NotNull(kept);
        Assert.Equal(BackgroundTaskState.Succeeded, kept!.State);
        Assert.Equal("THE REPORT", kept.Result);
        Assert.Equal("t2", next.Submit("another objective"));

        // /task clear forgets the tasks, never their numbers.
        await WaitUntil(() => next.Get("t2")?.IsFinished == true, "t2 finishes");
        next.ClearFinished();
        Assert.Null(Queue((_, _, _) => Report("x")).Get("t1"));
        Assert.Equal("t3", Queue((_, _, _) => Report("x")).Submit("a third"));
    }

    [Fact]
    public async Task TasksTheClosingStopped_AreReadBackStoppedByIt_RunningAndQueued()
    {
        var first = Queue(Forever);
        first.Submit("running when it closed");
        first.Submit("still queued");
        await WaitUntil(() => first.Get("t1")?.StepsTaken == 1, "t1 takes its step");
        first.Dispose();

        var next = Queue((_, _, _) => Report("x"));
        var running = next.Get("t1")!;
        var queued  = next.Get("t2")!;
        Assert.Equal((BackgroundTaskState.Cancelled, TaskEndCause.EditorClosed), (running.State, running.Cause));
        Assert.Equal((BackgroundTaskState.Cancelled, TaskEndCause.EditorClosed), (queued.State, queued.Cause));
        Assert.Equal(Strings.TaskStoppedByClose(1), TaskCommandHandler.CauseLine(running));
        Assert.Equal(Strings.TaskStoppedByCloseBeforeStart, TaskCommandHandler.CauseLine(queued));
        Assert.Contains("`/task retry t1`", TaskCommandHandler.RenderList(next), StringComparison.Ordinal);
    }

    /// <summary>A process killed without closing leaves its tasks unfinished in the journal: the next start calls them
    /// stopped. A queue that still runs keeps its own — neither shown elsewhere nor called stopped, and its number taken.</summary>
    [Fact]
    public async Task AnUnfinishedTask_IsStoppedOnlyWhenItsQueueIsGone()
    {
        new TaskJournal(File).Update(entries =>
        {
            entries.Add(new TaskJournal.Entry("t1", "killed with its process", BackgroundTaskState.Running,
                                              DateTimeOffset.UtcNow, new TaskJournal.Owner(int.MaxValue, 1, "gone"),
                                              StartedAt: DateTimeOffset.UtcNow, StepsTaken: 3, WorkspaceRoot: Root));
            return true;
        });
        var live = Queue(Forever);
        Assert.Equal("t2", live.Submit("running in another window"));
        await WaitUntil(() => live.Get("t2")?.State == BackgroundTaskState.Running, "t2 runs");

        var elsewhere = Queue((_, _, _) => Report("x"));
        Assert.Equal(TaskEndCause.EditorClosed, elsewhere.Get("t1")?.Cause);
        Assert.Null(elsewhere.Get("t2"));                            // another window's, running there
        Assert.Equal("t3", elsewhere.Submit("a third"));             // its number is still taken
    }

    /// <summary>One journal per editor: two workspaces never name two tasks alike, and each workspace lists its own.</summary>
    [Fact]
    public async Task Numbers_AreTheEditors_AndEachWorkspaceListsItsOwnTasks()
    {
        var other = Path.Combine(_dir, "other");
        var a = Queue((_, _, _) => Report("a"));
        var b = Queue((_, _, _) => Report("b"), root: () => other);
        Assert.Equal("t1", a.Submit("in the first"));
        Assert.Equal("t2", b.Submit("in the second"));
        await WaitUntil(() => a.Get("t1")?.IsFinished == true && b.Get("t2")?.IsFinished == true, "both finish");
        a.Dispose();
        b.Dispose();

        var again = Queue((_, _, _) => Report("x"));
        Assert.NotNull(again.Get("t1"));
        Assert.Null(again.Get("t2"));   // the other workspace's
    }

    /// <summary>A task waiting for the server while another solution is opened does not start there when it answers.</summary>
    [Fact]
    public async Task ATaskWhoseWorkspaceWasLeftWhileItWaitedForTheServer_DoesNotRunThere()
    {
        var down = true;
        var root = Root;
        var ran = false;
        var queue = Queue((_, _, _) => { ran = true; return Report("x"); },
                          serverDown: _ => Task.FromResult(Volatile.Read(ref down) ? "Ollama at http://localhost:11434" : null),
                          root: () => Volatile.Read(ref root));
        queue.Submit("about the first solution");
        await WaitUntil(() => queue.Get("t1")?.WaitingForServer is not null, "t1 waits");
        Volatile.Write(ref root, Path.Combine(_dir, "another-solution"));
        Volatile.Write(ref down, false);

        await WaitUntil(() => queue.Get("t1")?.IsFinished == true, "t1 ends");
        Assert.False(ran);
        Assert.Equal(Strings.TaskWorkspaceChanged, queue.Get("t1")!.Error);
    }

    [Fact]
    public async Task TwoQueuesOfOneWorkspace_NeverGiveTheSameNumber()
    {
        var a = Queue((_, _, _) => Report("a"));
        var b = Queue((_, _, _) => Report("b"));
        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => (i % 2 == 0 ? a : b).Submit($"task {i}"))));
        Assert.Equal(8, ids.Distinct().Count());
        Assert.All(ids, id => Assert.NotNull(id));
    }

    [Fact]
    public async Task AQueue_WaitsForItsServer_AndStartsItsTasksWhenItAnswers()
    {
        var down = true;
        var started = new List<string>();
        var queue = Queue((task, _, _) => { lock (started) started.Add(task.Id); return Report("done"); },
                          serverDown: _ => Task.FromResult(Volatile.Read(ref down) ? "LM Studio at http://localhost:1234" : null));
        queue.Submit("explain the refunds");
        queue.Submit("list the endpoints");
        await WaitUntil(() => queue.List().Count(t => t.WaitingForServer is not null) == 2, "both wait for the server");

        var list = TaskCommandHandler.RenderList(queue);
        Assert.Contains(Strings.TaskQueueWaitsForServer("LM Studio at http://localhost:1234", "`t1`, `t2`", 2), list, StringComparison.Ordinal);
        Assert.Contains(Strings.TaskStateWaitingAt(1), list, StringComparison.Ordinal);
        Assert.Contains(Strings.TaskStateWaitingAt(2), list, StringComparison.Ordinal);
        Assert.True(list.IndexOf("| `t2`", StringComparison.Ordinal) < list.IndexOf("| `t1`", StringComparison.Ordinal));   // newest first
        lock (started) Assert.Empty(started);

        Volatile.Write(ref down, false);
        await WaitUntil(() => queue.List().All(t => t.State == BackgroundTaskState.Succeeded), "both run once it answers");
        Assert.DoesNotContain(Strings.TaskStateWaitingAt(1), TaskCommandHandler.RenderList(queue), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATaskWhoseServerStopsDuringItsRun_SaysSo_AndTheNextOneWaits()
    {
        var down = false;
        var queue = Queue((task, onStep, _) =>
        {
            if (task.Id == "t2") return Report("ran");
            onStep("read a file");
            onStep("read another");
            Volatile.Write(ref down, true);
            return Task.FromResult(new BackgroundTaskQueue.TaskRunOutcome(string.Empty, [], Failure: "No connection could be made"));
        }, serverDown: _ => Task.FromResult(Volatile.Read(ref down) ? "Ollama at http://localhost:11434" : null));
        queue.Submit("find the callers");
        await WaitUntil(() => queue.Get("t1")?.IsFinished == true, "t1 ends");
        queue.Submit("next one");
        await WaitUntil(() => queue.Get("t2")?.WaitingForServer is not null, "t2 waits");

        var failed = queue.Get("t1")!;
        Assert.Equal((BackgroundTaskState.Failed, TaskEndCause.ServerStopped), (failed.State, failed.Cause));
        var at = failed.FinishedAt!.Value.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture);
        Assert.Equal(Strings.TaskServerStopped(2, at), TaskCommandHandler.CauseLine(failed));
        Assert.Contains(TaskCommandHandler.CauseLine(failed)!, TaskCommandHandler.FinishedNotice(failed), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retry_RunsAFinishedTaskAgainUnderANewNumber_InItsMode()
    {
        var runs = 0;
        var queue = Queue((_, _, _) => Interlocked.Increment(ref runs) == 1
            ? Task.FromResult(new BackgroundTaskQueue.TaskRunOutcome(string.Empty, [], Failure: "refused"))
            : Report("second time"));
        queue.Submit("audit the tax module", proposeWrites: true);
        await WaitUntil(() => queue.Get("t1")?.IsFinished == true, "t1 fails");

        var retried = TaskCommandHandler.Handle(queue, ["/task", "retry", "t1"]).Message;
        Assert.Equal(Strings.TaskRetried("t2", "t1", "audit the tax module"), retried);
        await WaitUntil(() => queue.Get("t2")?.IsFinished == true, "t2 runs");
        Assert.True(queue.Get("t2")!.ProposeWrites);
        Assert.Equal("second time", queue.Get("t2")!.Result);

        Assert.Equal(Strings.TaskUnknown("t9"), TaskCommandHandler.Handle(queue, ["/task", "retry", "t9"]).Message);
        // Free text after the word is an objective, as with stop.
        Assert.Equal(Strings.TaskSubmitted("t3", "retry the flaky build"),
                     TaskCommandHandler.Handle(queue, ["/task", "retry", "the", "flaky", "build"]).Message);

        // A task of the workspace left behind is refused at once, its number never spent failing.
        var root = Root;
        var moving = Queue((_, _, _) => Report("x"), root: () => Volatile.Read(ref root));
        var here = moving.Submit("about this solution")!;
        await WaitUntil(() => moving.Get(here)?.IsFinished == true, "it runs");
        Volatile.Write(ref root, Path.Combine(_dir, "another-solution"));
        Assert.Equal(Strings.TaskWorkspaceChanged, TaskCommandHandler.Handle(moving, ["/task", "retry", here]).Message);
    }

    [Fact]
    public async Task Retry_IsRefused_ForATaskStillRunning()
    {
        var queue = Queue(Forever);
        queue.Submit("long one");
        await WaitUntil(() => queue.Get("t1")?.State == BackgroundTaskState.Running, "t1 runs");
        Assert.Equal(Strings.TaskRetryNotFinished("t1"), TaskCommandHandler.Handle(queue, ["/task", "retry", "t1"]).Message);
    }

    [Fact]
    public async Task AReportOpened_IsRememberedAcrossARestart()
    {
        var first = Queue((_, _, _) => Report("r"));
        first.Submit("objective");
        await WaitUntil(() => first.Get("t1")?.IsFinished == true, "t1 finishes");
        Assert.False(first.Get("t1")!.Read);
        TaskCommandHandler.Handle(first, ["/task", "t1"]);
        first.Dispose();
        Assert.True(Queue((_, _, _) => Report("x")).Get("t1")!.Read);
    }

    [Fact]
    public void AJournalThatCannotBeRead_IsKeptAside_Said_AndNeverWrittenOver()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, "{ \"tasks\": [ { \"id\": \"t1\", ");
        var queue = Queue((_, _, _) => Report("x"));

        var aside = Assert.Single(queue.SetAside);
        Assert.Equal("{ \"tasks\": [ { \"id\": \"t1\", ", System.IO.File.ReadAllText(aside));
        Assert.Contains(Strings.TaskJournalSetAside(aside), TaskCommandHandler.RenderList(queue), StringComparison.Ordinal);
        Assert.Equal("t2", queue.Submit("works again"));   // the queue goes on, above the number the copy holds
    }

    /// <summary>A running task's steps are written along: an editor killed mid-run leaves how far its task got.</summary>
    [Fact]
    public async Task ARunningTasksSteps_AreWrittenAlong()
    {
        var queue = Queue(async (task, onStep, ct) =>
        {
            for (var i = 0; i < 5; i++) onStep($"step {i}");
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            return BackgroundTaskQueue.TaskRunOutcome.Of("never");
        });
        queue.Submit("long investigation");
        await WaitUntil(() => new TaskJournal(File).Read().Entries?.SingleOrDefault()?.StepsTaken == 5, "the steps are written");
        Assert.Equal(BackgroundTaskState.Running, new TaskJournal(File).Read().Entries!.Single().State);
    }

    /// <summary>Damaged after the queue read it, the journal is set aside by the next write — and that is said too.</summary>
    [Fact]
    public void AJournalDamagedAfterItWasRead_IsSaid_WhenTheNextWriteSetsItAside()
    {
        var queue = Queue((_, _, _) => Report("x"));
        Assert.Empty(queue.SetAside);
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, "not json");

        queue.Submit("after the damage");
        var aside = Assert.Single(queue.SetAside);
        Assert.Equal("not json", System.IO.File.ReadAllText(aside));
    }
}
