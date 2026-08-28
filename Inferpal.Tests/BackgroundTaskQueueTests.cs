using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Lifecycle of <c>/task</c>'s serial queue (ROADMAP §9). Everything the queue depends on — the
/// runner, the chat-idle gate, the clock — is injected, so these run without a backend or a GPU.
/// Tasks are driven by explicit gates rather than delays: a sleeping test is a flaky test.
/// </summary>
public class BackgroundTaskQueueTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Polls until <paramref name="condition"/> holds; fails the test on timeout.</summary>
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

    /// <summary>A runner the test drives step by step: it reports when a task enters, then blocks
    /// until the test releases it.</summary>
    private sealed class GatedRunner
    {
        private readonly Dictionary<string, TaskCompletionSource<string>> _gates = new();
        private readonly object _lock = new();

        public readonly List<string> Started = [];
        public int Concurrent;
        public int MaxConcurrent;

        public TaskCompletionSource<string> Gate(string id)
        {
            lock (_lock)
            {
                if (!_gates.TryGetValue(id, out var tcs))
                    _gates[id] = tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                return tcs;
            }
        }

        /// <summary>Modes the queue handed each started task, in order (roadmap §18).</summary>
        public List<bool> ProposeModes { get; } = [];

        public async Task<BackgroundTaskQueue.TaskRunOutcome> RunAsync(
            BackgroundTaskSnapshot task, Action<string> onStep, CancellationToken ct)
        {
            lock (_lock)
            {
                Started.Add(task.Id);
                ProposeModes.Add(task.ProposeWrites);
                MaxConcurrent = Math.Max(MaxConcurrent, ++Concurrent);
            }
            try
            {
                onStep($"working on {task.Id}");
                // Racing the gate against cancellation is what a real runner does with its token.
                var cancelled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (ct.Register(() => cancelled.TrySetCanceled(ct)))
                    return BackgroundTaskQueue.TaskRunOutcome.Of(
                        await await Task.WhenAny(Gate(task.Id).Task, cancelled.Task));
            }
            finally { lock (_lock) Concurrent--; }
        }
    }

    [Fact]
    public async Task Submit_RunsTheTask_AndReportsItsResult()
    {
        var runner = new GatedRunner();
        using var queue = new BackgroundTaskQueue(runner.RunAsync);

        var id = queue.Submit("audit the RAG layer");

        Assert.Equal("t1", id);
        await WaitUntil(() => queue.Get(id!)?.State == BackgroundTaskState.Running, "task to start");

        runner.Gate(id!).SetResult("all good");
        await WaitUntil(() => queue.Get(id!)!.IsFinished, "task to finish");

        var done = queue.Get(id!)!;
        Assert.Equal(BackgroundTaskState.Succeeded, done.State);
        Assert.Equal("all good", done.Result);
        Assert.Equal("audit the RAG layer", done.Objective);
        Assert.Contains($"working on {id}", done.Steps);
        Assert.NotNull(done.Duration);
    }

    [Fact]
    public async Task Tasks_RunOneAtATime_InSubmissionOrder()
    {
        var runner = new GatedRunner();
        using var queue = new BackgroundTaskQueue(runner.RunAsync);

        var first  = queue.Submit("first")!;
        var second = queue.Submit("second")!;
        var third  = queue.Submit("third")!;

        await WaitUntil(() => runner.Started.Count == 1, "only the first task to start");
        Assert.Equal(BackgroundTaskState.Queued, queue.Get(second)!.State);
        Assert.Equal(1, queue.Get(second)!.QueuePosition);
        Assert.Equal(2, queue.Get(third)!.QueuePosition);

        runner.Gate(first).SetResult("1");
        await WaitUntil(() => runner.Started.Count == 2, "the second task to start");
        runner.Gate(second).SetResult("2");
        runner.Gate(third).SetResult("3");
        await WaitUntil(() => queue.Get(third)!.IsFinished, "the third task to finish");

        Assert.Equal([first, second, third], runner.Started);
        Assert.Equal(1, runner.MaxConcurrent);   // a single GPU slot, never two at once

        // The worker releases the slot just after the task reaches its terminal state, so this
        // must be awaited rather than asserted straight after IsFinished.
        await WaitUntil(() => queue.ActiveCount == 0, "the queue to drain");
    }

    [Fact]
    public async Task ATask_WaitsForTheChatToGoIdle_BeforeTakingTheSlot()
    {
        var chatIdle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner   = new GatedRunner();
        using var queue = new BackgroundTaskQueue(runner.RunAsync, waitForChatIdle: _ => chatIdle.Task);

        var id = queue.Submit("background audit")!;

        // The gate is closed: the task must still be queued, not running.
        await Task.Delay(50);
        Assert.Equal(BackgroundTaskState.Queued, queue.Get(id)!.State);
        Assert.Empty(runner.Started);

        chatIdle.SetResult(true);
        await WaitUntil(() => queue.Get(id)!.State == BackgroundTaskState.Running, "task to start once the chat is idle");
    }

    [Fact]
    public async Task Cancel_WhileWaitingAtTheGpuGate_ReallyCancels_AndFinishesExactlyOnce()
    {
        // The worker dequeues a job (it becomes _current) and only flips it to Running once the
        // GPU gate opens — so a job can sit in state Queued, OUT of _pending, for minutes. The
        // old Cancel took the drop-branch for it: marked Cancelled without cancelling the CTS,
        // then the gate opened and the run executed anyway, finishing a second time into the
        // list (pre-1.6.0 architecture review, §1.6).
        var gate   = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new GatedRunner();
        using var queue = new BackgroundTaskQueue(runner.RunAsync, waitForChatIdle: ct => gate.Task.WaitAsync(ct));

        var id = queue.Submit("audit")!;
        await Task.Delay(50);   // let the worker pick it up; the gate keeps it in state Queued
        Assert.Equal(BackgroundTaskState.Queued, queue.Get(id)!.State);

        Assert.True(queue.Cancel(id));
        await WaitUntil(() => queue.Get(id)!.IsFinished, "the cancelled task to settle");
        Assert.Equal(BackgroundTaskState.Cancelled, queue.Get(id)!.State);

        gate.TrySetResult(true);   // the chat goes idle afterwards — a zombie would start now
        await Task.Delay(80);
        Assert.Empty(runner.Started);                              // the run never executed
        Assert.Equal(1, queue.List().Count(t => t.Id == id));      // finished exactly once
    }

    [Fact]
    public async Task ARunnerFailure_FailsTheTask_AndKeepsTheQueueGoing()
    {
        var runner = new GatedRunner();
        using var queue = new BackgroundTaskQueue(runner.RunAsync);

        var failing = queue.Submit("boom")!;
        var next    = queue.Submit("next")!;

        await WaitUntil(() => queue.Get(failing)!.State == BackgroundTaskState.Running, "first task to start");
        runner.Gate(failing).SetException(new InvalidOperationException("backend unreachable"));

        await WaitUntil(() => queue.Get(failing)!.IsFinished, "failing task to settle");
        Assert.Equal(BackgroundTaskState.Failed, queue.Get(failing)!.State);
        Assert.Equal("backend unreachable", queue.Get(failing)!.Error);

        // The worker must survive it — a crashed task cannot strand everything behind it.
        await WaitUntil(() => queue.Get(next)!.State == BackgroundTaskState.Running, "next task to start anyway");
    }

    [Fact]
    public async Task Cancel_OnARunningTask_SettlesItAsCancelled()
    {
        var runner = new GatedRunner();
        using var queue = new BackgroundTaskQueue(runner.RunAsync);

        var id = queue.Submit("long one")!;
        await WaitUntil(() => queue.Get(id)!.State == BackgroundTaskState.Running, "task to start");

        Assert.True(queue.Cancel(id));
        await WaitUntil(() => queue.Get(id)!.IsFinished, "task to settle");
        Assert.Equal(BackgroundTaskState.Cancelled, queue.Get(id)!.State);
    }

    [Fact]
    public async Task Cancel_OnAQueuedTask_DropsIt_WithoutEverRunningIt()
    {
        var runner = new GatedRunner();
        using var queue = new BackgroundTaskQueue(runner.RunAsync);

        var running = queue.Submit("running")!;
        var queued  = queue.Submit("queued")!;
        await WaitUntil(() => queue.Get(running)!.State == BackgroundTaskState.Running, "first task to start");

        Assert.True(queue.Cancel(queued));
        Assert.Equal(BackgroundTaskState.Cancelled, queue.Get(queued)!.State);

        runner.Gate(running).SetResult("done");
        await WaitUntil(() => queue.ActiveCount == 0, "the queue to drain");

        Assert.DoesNotContain(queued, runner.Started);
    }

    [Fact]
    public async Task Cancel_IsFalse_ForUnknownAndFinishedTasks()
    {
        var runner = new GatedRunner();
        using var queue = new BackgroundTaskQueue(runner.RunAsync);

        Assert.False(queue.Cancel("t42"));

        var id = queue.Submit("quick")!;
        await WaitUntil(() => queue.Get(id)!.State == BackgroundTaskState.Running, "task to start");
        runner.Gate(id).SetResult("ok");
        await WaitUntil(() => queue.Get(id)!.IsFinished, "task to finish");

        Assert.False(queue.Cancel(id));
    }

    [Fact]
    public async Task TaskFinished_FiresOncePerTask_WithItsTerminalSnapshot()
    {
        var runner = new GatedRunner();
        using var queue = new BackgroundTaskQueue(runner.RunAsync);

        var seen = new List<BackgroundTaskSnapshot>();
        queue.TaskFinished += s => { lock (seen) seen.Add(s); };

        var id = queue.Submit("notify me")!;
        await WaitUntil(() => queue.Get(id)!.State == BackgroundTaskState.Running, "task to start");
        runner.Gate(id).SetResult("report");

        await WaitUntil(() => { lock (seen) return seen.Count == 1; }, "the completion notification");
        Assert.Equal(id, seen[0].Id);
        Assert.Equal(BackgroundTaskState.Succeeded, seen[0].State);
        Assert.Equal("report", seen[0].Result);
    }

    [Fact]
    public void Submit_IsRefused_WhenTheQueueIsFull()
    {
        // Never released: every submission stays unfinished, so the cap is reached deterministically.
        var runner = new GatedRunner();
        using var queue = new BackgroundTaskQueue(runner.RunAsync);

        for (int i = 0; i < BackgroundTaskQueue.MaxPending; i++)
            Assert.NotNull(queue.Submit($"task {i}"));

        Assert.Null(queue.Submit("one too many"));
    }

    [Fact]
    public async Task StepJournal_IsCapped_AndKeepsTheTail()
    {
        var released = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var queue = new BackgroundTaskQueue((_, onStep, _) =>
        {
            for (int i = 0; i < BackgroundTaskQueue.MaxSteps + 50; i++) onStep($"step {i}");
            return released.Task.ContinueWith(t => BackgroundTaskQueue.TaskRunOutcome.Of(t.Result));
        });

        var id = queue.Submit("chatty")!;
        await WaitUntil(() => queue.Get(id)!.Steps.Count > 0, "steps to arrive");
        released.SetResult("done");
        await WaitUntil(() => queue.Get(id)!.IsFinished, "task to finish");

        var steps = queue.Get(id)!.Steps;
        Assert.Equal(BackgroundTaskQueue.MaxSteps, steps.Count);
        Assert.StartsWith("[…", steps[0]);                                        // the drop is marked
        Assert.Equal($"step {BackgroundTaskQueue.MaxSteps + 49}", steps[^1]);      // the tail survived
    }

    [Fact]
    public async Task ClearFinished_ForgetsFinishedTasks_ButNotLiveOnes()
    {
        var runner = new GatedRunner();
        using var queue = new BackgroundTaskQueue(runner.RunAsync);

        var first = queue.Submit("first")!;
        await WaitUntil(() => queue.Get(first)!.State == BackgroundTaskState.Running, "first task to start");
        runner.Gate(first).SetResult("ok");
        await WaitUntil(() => queue.Get(first)!.IsFinished, "first task to finish");

        var second = queue.Submit("second")!;
        await WaitUntil(() => queue.Get(second)!.State == BackgroundTaskState.Running, "second task to start");

        Assert.Equal(1, queue.ClearFinished());
        Assert.Null(queue.Get(first));
        Assert.NotNull(queue.Get(second));
    }

    [Fact]
    public async Task Dispose_CancelsWhatIsInFlight()
    {
        var runner = new GatedRunner();
        var queue  = new BackgroundTaskQueue(runner.RunAsync);

        var id = queue.Submit("in flight")!;
        await WaitUntil(() => queue.Get(id)!.State == BackgroundTaskState.Running, "task to start");

        queue.Dispose();

        await WaitUntil(() => queue.Get(id)!.IsFinished, "task to unwind on shutdown");
        Assert.Equal(BackgroundTaskState.Cancelled, queue.Get(id)!.State);
        Assert.Null(queue.Submit("after disposal"));
    }
}
