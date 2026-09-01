namespace Inferpal.Services.Tasks;

/// <summary>
/// Serial queue of detached agent runs (<c>/task</c>) — ROADMAP §9. A task is submitted from the
/// chat, leaves the conversation, and runs on its own while the user keeps coding; its report is
/// collected afterwards instead of interrupting.
/// </summary>
/// <remarks>
/// <para><b>One slot, chat first.</b> Inferpal talks to a single backend on a single GPU, so tasks
/// run strictly one at a time and each one waits for the chat to go idle before taking the slot
/// (<see cref="Hardware.GpuScheduler.WaitForChatIdleAsync"/>, injected). Granularity is the whole
/// task: once started it holds its own chat lease through <c>RunAgentAsync</c>, so a chat turn
/// opened meanwhile contends with it rather than preempting it — the honest limit of running an
/// agent loop we cannot suspend mid-flight. Interactive work is never *blocked*, only slowed.</para>
/// <para><b>No side effects in V1.</b> The runner is handed a read-only registry by the caller
/// (see <c>BackgroundTaskToolRegistry</c>): a background run explores and reports, it does not
/// write, delete or execute. This is a deliberate departure from the roadmap's "approvals batched
/// at submission" sketch, which would mean consenting to writes before knowing what they are —
/// a blank cheque, and precisely what the approval prompt exists to prevent. Mutating background
/// tasks, if they ever ship, must present their diffs for approval when they report back, never
/// before.</para>
/// <para>Pure and editor-agnostic: the runner, the idle gate and the clock are all injected, so
/// the whole lifecycle is testable without a backend, a GPU or Visual Studio.</para>
/// </remarks>
internal sealed class BackgroundTaskQueue : IDisposable
{
    /// <summary>Hard cap on unfinished tasks. Submitting beyond it is refused, not silently dropped.</summary>
    internal const int MaxPending = 10;

    /// <summary>Cap on a task's step journal; past it the oldest entries go, with a marker.</summary>
    internal const int MaxSteps = 200;

    private const string TrimMarker = "[… earlier steps dropped …]";

    /// <summary>What a completed run hands back: its report, and the changes it proposed.</summary>
    /// <param name="Report">Final markdown answer.</param>
    /// <param name="Proposals">
    /// Changes recorded but not applied (roadmap §18). Empty for a read-only task. Carried here
    /// rather than folded into the report because <c>/task apply</c> needs the structured diff, not
    /// prose about it.
    /// </param>
    internal sealed record TaskRunOutcome(string Report, IReadOnlyList<TaskProposal> Proposals)
    {
        /// <summary>A read-only run: report only.</summary>
        public static TaskRunOutcome Of(string report) => new(report, []);
    }

    /// <summary>Runs one task to completion. Returns its outcome; may throw to fail the task.</summary>
    /// <param name="task">Snapshot of the task being started (id, objective, and whether it proposes).</param>
    /// <param name="onStep">Progress sink, mirrored into the task's journal.</param>
    internal delegate Task<TaskRunOutcome> TaskRunner(BackgroundTaskSnapshot task, Action<string> onStep, CancellationToken ct);

    private sealed class Job
    {
        public required string Id;
        public required string Objective;
        public required DateTimeOffset CreatedAt;
        public bool ProposeWrites;
        public IReadOnlyList<TaskProposal> Proposals = [];

        public BackgroundTaskState State = BackgroundTaskState.Queued;
        /// <summary>Set when Cancel targets a job the worker already picked but whose CTS may not
        /// be wired yet (the gate-wait window) — RunJobAsync honours it right after wiring.</summary>
        public bool CancelRequested;
        public DateTimeOffset? StartedAt;
        public DateTimeOffset? FinishedAt;
        public string? Result;
        public string? Error;
        public readonly List<string> Steps = [];
        public CancellationTokenSource? Cts;
    }

    private readonly TaskRunner                    _runner;
    private readonly Func<CancellationToken, Task> _waitForChatIdle;
    private readonly Func<DateTimeOffset>          _now;

    private readonly object                  _lock = new();
    private readonly Queue<Job>              _pending = new();
    private readonly List<Job>               _finished = [];
    private readonly CancellationTokenSource _shutdown = new();

    private Job?  _current;
    private Task? _worker;
    private int   _seq;
    private bool  _disposed;

    /// <param name="runner">Executes a task (the real one drives <c>RunAgentAsync</c>).</param>
    /// <param name="waitForChatIdle">Yield point before taking the slot; defaults to the GPU scheduler.</param>
    /// <param name="now">Clock, overridable so tests can assert timings.</param>
    internal BackgroundTaskQueue(
        TaskRunner runner,
        Func<CancellationToken, Task>? waitForChatIdle = null,
        Func<DateTimeOffset>? now = null)
    {
        _runner          = runner;
        _waitForChatIdle = waitForChatIdle ?? Hardware.GpuScheduler.WaitForChatIdleAsync;
        _now             = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Raised once per task when it reaches a terminal state — the front-end's notification
    /// hook. Invoked outside the lock; a throwing handler is swallowed, never fails the task.</summary>
    internal event Action<BackgroundTaskSnapshot>? TaskFinished;

    // ── Submission ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Submits <paramref name="objective"/> and returns its short id (<c>t1</c>, <c>t2</c>, …),
    /// or <c>null</c> when the queue is full (<see cref="MaxPending"/>) or already disposed.
    /// </summary>
    internal string? Submit(string objective, bool proposeWrites = false)
    {
        Job job;
        lock (_lock)
        {
            if (_disposed) return null;
            if (_pending.Count + (_current is null ? 0 : 1) >= MaxPending) return null;

            job = new Job
            {
                Id = "t" + (++_seq), Objective = objective, CreatedAt = _now(),
                ProposeWrites = proposeWrites,
            };
            _pending.Enqueue(job);

            // The worker retires when it drains the queue (under this same lock), so a null
            // _worker here means nothing is pumping and this submission must restart it.
            _worker ??= Task.Run(WorkerLoopAsync);
        }
        return job.Id;
    }

    // ── Inspection ──────────────────────────────────────────────────────────────

    /// <summary>Every known task — running first, then queued in order, then finished (newest last).</summary>
    internal IReadOnlyList<BackgroundTaskSnapshot> List()
    {
        lock (_lock)
        {
            var snapshots = new List<BackgroundTaskSnapshot>();
            if (_current is not null) snapshots.Add(SnapshotLocked(_current));
            snapshots.AddRange(_pending.Select(SnapshotLocked));
            snapshots.AddRange(_finished.Select(SnapshotLocked));
            return snapshots;
        }
    }

    /// <summary>The task with this id (case-insensitive), or null.</summary>
    internal BackgroundTaskSnapshot? Get(string id)
    {
        lock (_lock)
        {
            var job = FindLocked(id);
            return job is null ? null : SnapshotLocked(job);
        }
    }

    /// <summary>Count of tasks not yet finished (running + queued) — for the status line.</summary>
    internal int ActiveCount
    {
        get { lock (_lock) return _pending.Count + (_current is null ? 0 : 1); }
    }

    // ── Cancellation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Cancels a task. A queued one is dropped immediately; a running one is asked to stop and
    /// settles as <see cref="BackgroundTaskState.Cancelled"/> when its runner unwinds. Returns
    /// false for an unknown or already finished id.
    /// </summary>
    internal bool Cancel(string id)
    {
        BackgroundTaskSnapshot? dropped = null;
        lock (_lock)
        {
            var job = FindLocked(id);
            if (job is null || IsTerminal(job.State)) return false;

            // ⚠ "Queued" does not mean "still in _pending": the worker dequeues the job and only
            // flips it to Running AFTER the GPU gate opens (WaitForChatIdleAsync), so a job can sit
            // as _current in state Queued for minutes. Taking the drop-branch for it marked the
            // task Cancelled without cancelling its CTS — the run then executed anyway and
            // finished a second time into _finished (pre-1.6.0 architecture review, §1.6). The drop-branch is
            // therefore gated on actual _pending membership.
            if (job.State == BackgroundTaskState.Queued && _pending.Contains(job))
            {
                // Rebuild the queue without it: Queue<T> has no remove, and dequeuing in the
                // worker instead would let a cancelled task briefly look like it is starting.
                var kept = _pending.Where(j => !ReferenceEquals(j, job)).ToList();
                _pending.Clear();
                foreach (var j in kept) _pending.Enqueue(j);

                FinishLocked(job, BackgroundTaskState.Cancelled, null, null);
                dropped = SnapshotLocked(job);
            }
            else
            {
                // Running, or picked-up-and-gate-waiting: the runner observes the token and
                // unwinds. The flag covers the sliver before RunJobAsync wires job.Cts.
                job.CancelRequested = true;
                job.Cts?.Cancel();
            }
        }

        if (dropped is not null) RaiseFinished(dropped);
        return true;
    }

    /// <summary>Forgets every finished task. Running and queued ones are untouched.</summary>
    internal int ClearFinished()
    {
        lock (_lock)
        {
            var count = _finished.Count;
            _finished.Clear();
            return count;
        }
    }

    // ── Worker ──────────────────────────────────────────────────────────────────

    private async Task WorkerLoopAsync()
    {
        while (true)
        {
            Job job;
            lock (_lock)
            {
                if (_pending.Count == 0 || _disposed)
                {
                    _worker = null;   // retire; the next Submit restarts a worker
                    return;
                }
                job = _pending.Dequeue();
                _current = job;
            }

            var snapshot = await RunJobAsync(job).ConfigureAwait(false);

            lock (_lock) _current = null;
            RaiseFinished(snapshot);
        }
    }

    private async Task<BackgroundTaskSnapshot> RunJobAsync(Job job)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        lock (_lock)
        {
            job.Cts = cts;
            if (job.CancelRequested) cts.Cancel();   // Cancel raced the wiring — honour it now
        }

        try
        {
            // Chat first: hold the task at the gate while an interactive turn is in flight.
            await _waitForChatIdle(cts.Token).ConfigureAwait(false);

            BackgroundTaskSnapshot started;
            lock (_lock)
            {
                job.State     = BackgroundTaskState.Running;
                job.StartedAt = _now();
                started       = SnapshotLocked(job);
            }

            var outcome = await _runner(started, step => AppendStep(job, step), cts.Token).ConfigureAwait(false);

            lock (_lock)
            {
                job.Proposals = outcome.Proposals;
                FinishLocked(job, BackgroundTaskState.Succeeded, outcome.Report, null);
                return SnapshotLocked(job);
            }
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                FinishLocked(job, BackgroundTaskState.Cancelled, null, null);
                return SnapshotLocked(job);
            }
        }
        catch (Exception ex)
        {
            // A background failure has no chat bubble to land in: record it on the task and trace
            // it, so `/task <id>` and `/diagnostics` both explain a run that produced nothing.
            Diagnostics.Swallow($"BackgroundTaskQueue({job.Id})", ex);
            lock (_lock)
            {
                FinishLocked(job, BackgroundTaskState.Failed, null, ex.Message);
                return SnapshotLocked(job);
            }
        }
        finally
        {
            lock (_lock) job.Cts = null;
        }
    }

    private void AppendStep(Job job, string step)
    {
        if (string.IsNullOrWhiteSpace(step)) return;
        lock (_lock)
        {
            job.Steps.Add(step);
            if (job.Steps.Count <= MaxSteps) return;

            // Keep the tail: on a long run the recent steps explain where it got to.
            job.Steps.RemoveRange(0, job.Steps.Count - MaxSteps);
            job.Steps[0] = TrimMarker;
        }
    }

    // ── Helpers (all callers hold _lock) ────────────────────────────────────────

    private static bool IsTerminal(BackgroundTaskState state) =>
        state is BackgroundTaskState.Succeeded or BackgroundTaskState.Failed or BackgroundTaskState.Cancelled;

    private Job? FindLocked(string id) =>
        _current is not null && string.Equals(_current.Id, id, StringComparison.OrdinalIgnoreCase) ? _current
        : _pending.FirstOrDefault(j => string.Equals(j.Id, id, StringComparison.OrdinalIgnoreCase))
       ?? _finished.FirstOrDefault(j => string.Equals(j.Id, id, StringComparison.OrdinalIgnoreCase));

    private void FinishLocked(Job job, BackgroundTaskState state, string? result, string? error)
    {
        job.State      = state;
        job.FinishedAt = _now();
        job.Result     = result;
        job.Error      = error;
        // ⚠ Leaving "current" and entering "finished" is ONE transition, and it happens under
        // ONE lock. It used to be two: this method appended to _finished, and the worker loop
        // cleared _current afterwards in a lock of its own. Between the two the job was in both
        // places at once, so List() returned it TWICE and Count counted it twice — for a window
        // the caller cannot see or avoid. Caught by CI on 2026-09-01, on a run where the local
        // machine had passed the same test twice: the collection held two identical snapshots of
        // t1, both already Succeeded.
        // ReferenceEquals, not an id comparison: a pending job cancelled before it ever started
        // also finishes here, and it was never _current.
        if (ReferenceEquals(_current, job)) _current = null;
        _finished.Add(job);
    }

    private BackgroundTaskSnapshot SnapshotLocked(Job job)
    {
        var position = 0;
        if (job.State == BackgroundTaskState.Queued)
        {
            var rank = 1;
            foreach (var pending in _pending)
            {
                if (ReferenceEquals(pending, job)) { position = rank; break; }
                rank++;
            }
        }

        return new BackgroundTaskSnapshot(
            job.Id, job.Objective, job.State, job.CreatedAt, job.StartedAt, job.FinishedAt,
            job.Result, job.Error, job.Steps.ToArray(), position,
            job.ProposeWrites, job.Proposals);
    }

    private void RaiseFinished(BackgroundTaskSnapshot snapshot)
    {
        try { TaskFinished?.Invoke(snapshot); }
        catch (Exception ex) { Diagnostics.Swallow("BackgroundTaskQueue.TaskFinished", ex); }
    }

    /// <summary>Cancels everything in flight. Detached runs must not outlive the editor.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try { _shutdown.Cancel(); } catch { }
        try { _shutdown.Dispose(); } catch { }
    }
}
