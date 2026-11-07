using Inferpal.Localization;

namespace Inferpal.Services.Tasks;

/// <summary>
/// Serial queue of detached agent runs (<c>/task</c>). A task is submitted from the chat, leaves
/// the conversation, and runs on its own while the user keeps coding; its report is collected
/// afterwards instead of interrupting.
/// </summary>
/// <remarks>
/// <para><b>One slot, chat first.</b> Inferpal talks to a single backend on a single GPU, so tasks
/// run strictly one at a time and each one waits for every turn to end before taking the slot — on the
/// whole machine, a turn waiting for a person at a card included (<see cref="Hardware.GpuScheduler.WaitForTurnsToEndAsync"/>,
/// injected): started beside it, the task would compete with the turn for the GPU as soon as the person answers. Granularity is the whole
/// task: once started it holds its own chat lease through <c>RunAgentAsync</c>, so a chat turn
/// opened meanwhile contends with it rather than preempting it — the honest limit of running an
/// agent loop we cannot suspend mid-flight. Interactive work is never *blocked*, only slowed.</para>
/// <para><b>No side effects.</b> The runner is handed a read-only registry by the caller (see
/// <c>BackgroundTaskToolRegistry</c>): a background run explores and reports, it does not write,
/// delete or execute. Batching approvals at submission would mean consenting to writes before
/// knowing what they are — a blank cheque, and precisely what the approval prompt exists to
/// prevent. Mutating background tasks, if they ever ship, must present their diffs for approval
/// when they report back, never before.</para>
/// <para>Pure and editor-agnostic: the runner, the idle gate and the clock are all injected, so
/// the whole lifecycle is testable without a backend, a GPU or Visual Studio.</para>
/// </remarks>
internal sealed class BackgroundTaskQueue : IDisposable
{
    /// <summary>Hard cap on unfinished tasks. Submitting beyond it is refused, not silently dropped.</summary>
    internal const int MaxPending = 10;

    /// <summary>Cap on a task's step journal; past it the oldest entries go, and the report counts them
    /// (<see cref="BackgroundTaskSnapshot.StepsTaken"/>).</summary>
    internal const int MaxSteps = 200;

    /// <summary>Finished tasks kept; past it the oldest that proposed nothing is forgotten. A task that proposed changes
    /// is never forgotten to make room: they are the person's to apply (<c>/task clear</c> forgets them).</summary>
    internal const int MaxFinished = 50;

    /// <summary>What a completed run hands back: its report, and the changes it proposed.</summary>
    /// <param name="Report">Final markdown answer.</param>
    /// <param name="Proposals">
    /// Changes recorded but not applied. Empty for a read-only task. Carried here
    /// rather than folded into the report because <c>/task apply</c> needs the structured diff, not
    /// prose about it.
    /// </param>
    /// <param name="Failure">Set when the run FAILED (backend down, request refused): the queue then fails the task
    /// with this cause instead of reporting the error message as a finished investigation.</param>
    internal sealed record TaskRunOutcome(string Report, IReadOnlyList<TaskProposal> Proposals, string? Failure = null)
    {
        /// <summary>A read-only run: report only.</summary>
        public static TaskRunOutcome Of(string report) => new(report, []);

        /// <summary>
        /// The outcome of an agent run: its answer as the report — followed, when the run was not a task
        /// carried to its end, by the line that says so — and the proposals.
        /// </summary>
        /// <remarks>
        /// ⚠ A report is read LATER, out of the conversation, as the result of an investigation: a run
        /// stopped on a repeat, or an answer that stopped at the length limit, must not read as a finished
        /// one. Same policy as the chat's (<see cref="Agent.ChatTurnPolicy.EndNotice"/>), for both runners.
        /// </remarks>
        public static TaskRunOutcome Of(AgentResult run, IReadOnlyList<TaskProposal> proposals)
        {
            // ⚠ The agent loop never throws for a backend failure — it returns the message as the answer — so a runner
            // never throws either, and the task finished "✅" with the error as its report.
            if (run.Failed) return new(string.Empty, proposals, Failure: run.FinalResponse);
            var notice = Agent.ChatTurnPolicy.EndNotice(run.ReachedIterationLimit, run.WasLoopDetected, run.AnswerCut,
                                                        answerRepeating: run.AnswerRepeating);
            return new(notice.Length == 0 ? run.FinalResponse : run.FinalResponse.TrimEnd() + "\n\n" + notice,
                       proposals);
        }
    }

    /// <summary>Runs one task to completion. Returns its outcome; may throw to fail the task.</summary>
    /// <param name="task">Snapshot of the task being started (id, objective, and whether it proposes).</param>
    /// <param name="onStep">Progress sink, mirrored into the task's journal.</param>
    internal delegate Task<TaskRunOutcome> TaskRunner(BackgroundTaskSnapshot task, Action<string> onStep, CancellationToken ct);

    /// <summary>Runs one task, recording what it proposes in <paramref name="recorder"/> — created by the queue for a task
    /// in proposal mode (<c>null</c> otherwise), so what a stopped or failed task had proposed is kept.</summary>
    /// <remarks>⚠ A recorder of the runner's own dies with the cancellation that ends the run: Stop would throw away every
    /// change the task had proposed, under a notice saying nothing was produced.</remarks>
    internal delegate Task<TaskRunOutcome> ProposingTaskRunner(
        BackgroundTaskSnapshot task, Action<string> onStep, ProposalRecorder? recorder, CancellationToken ct);

    private sealed class Job
    {
        public required string Id;
        public required string Objective;
        public required DateTimeOffset CreatedAt;
        /// <summary>The workspace the task was submitted in — the one its objective is about.</summary>
        public string? WorkspaceRoot;
        /// <summary>The active-file block at submission (<see cref="BackgroundTaskSnapshot.Context"/>).</summary>
        public string? Context;
        public bool ProposeWrites;
        public IReadOnlyList<TaskProposal> Proposals = [];
        /// <summary>Where a proposal-mode task records its changes while it runs.</summary>
        public ProposalRecorder? Recorder;

        public BackgroundTaskState State = BackgroundTaskState.Queued;
        /// <summary>Set when Cancel targets a job the worker already picked but whose CTS may not
        /// be wired yet (the gate-wait window) — RunJobAsync honours it right after wiring.</summary>
        public bool CancelRequested;
        public DateTimeOffset? StartedAt;
        public DateTimeOffset? FinishedAt;
        public string? Result;
        public string? Error;
        public readonly List<string> Steps = [];
        public int StepsTaken;
        public CancellationTokenSource? Cts;
        public TaskEndCause Cause;
        /// <summary>Its report was opened.</summary>
        public bool Read;
        /// <summary>The notice of a start said it, and the person dismissed it.</summary>
        public bool Said;
        /// <summary>Read from the journal, left by a queue that is gone: what the notice of a start may say.</summary>
        public bool Inherited;
    }

    private readonly ProposingTaskRunner           _runner;
    private readonly Func<CancellationToken, Task> _waitForChatIdle;
    private readonly Func<DateTimeOffset>          _now;
    private readonly Func<string?>?                _currentRoot;
    private readonly Func<string?>?                _submissionContext;

    private readonly object                  _lock = new();
    private readonly Queue<Job>              _pending = new();
    private readonly List<Job>               _finished = [];
    private readonly CancellationTokenSource _shutdown = new();

    private Job?  _current;
    private Task? _worker;
    private int   _seq;
    private bool  _disposed;

    /// <summary>How long a queue waiting for its server waits between two questions to it.</summary>
    internal static readonly TimeSpan ServerPollInterval = TimeSpan.FromSeconds(15);

    private readonly Func<string?, TaskJournal?>?            _journalFor;
    private readonly Func<CancellationToken, Task<string?>>? _serverDown;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TaskJournal.Owner                       _owner;
    /// <summary>The queues alive in this process, by id: an unfinished journal entry of one of them is its own.</summary>
    private static readonly HashSet<string> LiveQueues = [];
    /// <summary>The workspaces whose journal this queue has read.</summary>
    private readonly HashSet<string> _loadedRoots = new(PathComparer.Default);
    /// <summary>The server the queue waits for before its next task; <c>null</c> while it answers.</summary>
    private string? _waitingFor;
    /// <summary>The copies kept of journals that could not be read.</summary>
    private readonly List<string> _setAside = [];

    /// <param name="runner">Executes a task (the real one drives <c>RunAgentAsync</c>).</param>
    /// <param name="waitForChatIdle">Yield point before taking the slot; defaults to the GPU scheduler.</param>
    /// <param name="now">Clock, overridable so tests can assert timings.</param>
    /// <param name="currentRoot">The workspace open now: read at submission and again at the start (see
    /// <see cref="RunJobAsync"/>); <c>null</c> when the front-end's workspace cannot change.</param>
    /// <param name="submissionContext">The active-file block a question would carry now, read at submission
    /// (<see cref="BackgroundTaskSnapshot.Context"/>); <c>null</c> = none.</param>
    /// <param name="journalFor">The journal of a workspace (<see cref="TaskJournal"/>); <c>null</c>, or a <c>null</c>
    /// answer: tasks live in memory only.</param>
    /// <param name="serverDown">Asked before a task starts: the server, named, when it does not answer; <c>null</c> when it
    /// does. <c>null</c>: never asked.</param>
    /// <param name="delay">The wait between two questions to a server that does not answer; overridable for tests.</param>
    internal BackgroundTaskQueue(
        TaskRunner runner,
        Func<CancellationToken, Task>? waitForChatIdle = null,
        Func<DateTimeOffset>? now = null,
        Func<string?>? currentRoot = null,
        Func<string?>? submissionContext = null,
        Func<string?, TaskJournal?>? journalFor = null,
        Func<CancellationToken, Task<string?>>? serverDown = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        : this((task, onStep, _, ct) => runner(task, onStep, ct), waitForChatIdle, now, currentRoot, submissionContext,
               journalFor, serverDown, delay) { }

    /// <param name="runner">Executes a task, recording its proposals in the recorder the queue hands it.</param>
    /// <param name="waitForChatIdle">Yield point before taking the slot; defaults to the GPU scheduler.</param>
    /// <param name="now">Clock, overridable so tests can assert timings.</param>
    /// <param name="currentRoot">The workspace open now (see the other constructor).</param>
    /// <param name="submissionContext">The active-file block now (see the other constructor).</param>
    /// <param name="journalFor">The journal of a workspace (see the other constructor).</param>
    /// <param name="serverDown">Asked before a task starts (see the other constructor).</param>
    /// <param name="delay">The wait between two questions to a silent server (see the other constructor).</param>
    internal BackgroundTaskQueue(
        ProposingTaskRunner runner,
        Func<CancellationToken, Task>? waitForChatIdle = null,
        Func<DateTimeOffset>? now = null,
        Func<string?>? currentRoot = null,
        Func<string?>? submissionContext = null,
        Func<string?, TaskJournal?>? journalFor = null,
        Func<CancellationToken, Task<string?>>? serverDown = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _runner            = runner;
        _waitForChatIdle   = waitForChatIdle ?? Hardware.GpuScheduler.WaitForTurnsToEndAsync;
        _now               = now ?? (() => DateTimeOffset.UtcNow);
        _currentRoot       = currentRoot;
        _submissionContext = submissionContext;
        _journalFor        = journalFor;
        _serverDown        = serverDown;
        _delay             = delay ?? Task.Delay;
        _owner             = new(Environment.ProcessId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Guid.NewGuid().ToString("N"));
        lock (LiveQueues) LiveQueues.Add(_owner.Queue);
        EnsureLoaded();
    }

    /// <summary>
    /// The question a queue asks before a task starts, for both editors: the server, named, when it does not answer;
    /// <c>null</c> when it answers. Refusing is answering — a 401 is a running server, and the task then says why.
    /// </summary>
    internal static Func<CancellationToken, Task<string?>> ServerDown(Inference.IInferenceProvider client, Config.InferpalConfig config) =>
        async ct => await client.CheckConnectionAsync(config.BaseUrl, ct).ConfigureAwait(false) || client.ConnectionRefusal is not null
            ? null
            : Strings.TaskServerAt(Inference.InferenceProviderFactory.DisplayName(config.Provider), config.BaseUrl);

    /// <summary>The copies kept of journals that could not be read — said by <c>/task</c>.</summary>
    internal IReadOnlyList<string> SetAside { get { lock (_lock) return [.. _setAside]; } }

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
        // Read before the lock: it reads the editor's state and the repository's files.
        string? context = null;
        try { context = ActiveFileContext.ForFreshHistory(_submissionContext?.Invoke()); }
        catch (Exception ex) { Diagnostics.Swallow("BackgroundTaskQueue.SubmissionContext", ex); }
        return SubmitCore(objective, proposeWrites, string.IsNullOrWhiteSpace(context) ? null : context, _currentRoot?.Invoke());
    }

    /// <summary>
    /// Submits a finished task's objective again, in its mode, with the context and in the workspace it was first
    /// submitted with, under a new number. <c>null</c> when the queue is full or disposed.
    /// </summary>
    internal string? Retry(BackgroundTaskSnapshot task) =>
        SubmitCore(task.Objective, task.ProposeWrites, task.Context, task.WorkspaceRoot ?? _currentRoot?.Invoke());

    private string? SubmitCore(string objective, bool proposeWrites, string? context, string? root)
    {
        EnsureLoaded();
        int known;
        lock (_lock)
        {
            if (_disposed || _pending.Count + (_current is null ? 0 : 1) >= MaxPending) return null;
            known = _seq;
        }
        var job = new Job
        {
            Id = string.Empty, Objective = objective, CreatedAt = _now(),
            ProposeWrites = proposeWrites, WorkspaceRoot = root, Context = context,
        };
        // ⚠ The number is taken in the journal, under its lock: the file holds the numbers every window and every earlier
        // process gave, and a number given twice sends "/task t2" in a saved notice to another task.
        var journal = _journalFor?.Invoke(root);
        var reserved = journal?.Reserve(known, id =>
        {
            job.Id = id;
            return EntryOf(job);
        });
        if (journal?.TakeSetAside() is { } aside) lock (_lock) _setAside.Add(aside);

        var refused = false;
        lock (_lock)
        {
            if (_disposed || _pending.Count + (_current is null ? 0 : 1) >= MaxPending) refused = true;
            else
            {
                job.Id = reserved ?? "t" + (_seq + 1);
                _seq   = Math.Max(_seq, Number(job.Id));
                _pending.Enqueue(job);

                // The worker retires when it drains the queue (under this same lock), so a null
                // _worker here means nothing is pumping and this submission must restart it.
                _worker ??= Task.Run(WorkerLoopAsync);
            }
        }
        if (!refused) return job.Id;
        // Filled meanwhile, or closed: the number stays given, the entry goes.
        if (reserved is not null) Write(journal!, entries => entries.RemoveAll(e => e.Id == reserved));
        return null;
    }

    private static int Number(string id) => TaskJournal.Number(id);

    /// <summary>Records that the report of <paramref name="id"/> was opened.</summary>
    internal void MarkRead(string id)
    {
        Job? job;
        lock (_lock)
        {
            job = FindLocked(id);
            if (job is null || !IsTerminal(job.State) || job.Read) return;
            job.Read = true;
        }
        Persist(job);
    }

    /// <summary>The notice of a start was dismissed: the runs and tasks it said are not said again.</summary>
    internal void MarkSaid(IReadOnlyCollection<string> runIds, IReadOnlyCollection<string> taskIds)
    {
        lock (_lock)
            foreach (var id in taskIds)
                if (FindLocked(id) is { } job) job.Said = true;
        if (_journalFor?.Invoke(_currentRoot?.Invoke()) is not { } journal) return;
        journal.MarkSaid(runIds, taskIds);
        if (journal.TakeSetAside() is { } aside) lock (_lock) _setAside.Add(aside);
    }

    /// <summary>The runs a dismissed notice of this editor already said.</summary>
    internal IReadOnlySet<string> SaidRuns() =>
        _journalFor?.Invoke(_currentRoot?.Invoke())?.SaidRuns() ?? new HashSet<string>(StringComparer.Ordinal);

    // ── The journal ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the journal of the workspace open now, once per workspace: its finished tasks join the list, and the tasks it
    /// holds unfinished whose queue is gone are the ones the closing stopped — recorded as such.
    /// </summary>
    private void EnsureLoaded()
    {
        var root = _currentRoot?.Invoke();
        if (_journalFor is null || string.IsNullOrWhiteSpace(root)) return;
        var key = Persistence.SessionManager.NormalizeRoot(root);
        lock (_lock) if (!_loadedRoots.Add(key)) return;
        if (_journalFor(root) is not { } journal) return;

        var (entries, aside) = journal.Read();
        var stopped = new List<Job>();
        lock (_lock)
        {
            if (aside is not null) _setAside.Add(aside);
            // Not read (a lock held too long, a disk error): asked again at the next look, never taken as empty.
            if (entries is null) { _loadedRoots.Remove(key); return; }
            foreach (var entry in entries.OrderBy(e => e.CreatedAt))
            {
                _seq = Math.Max(_seq, Number(entry.Id));
                // The journal holds every workspace of this editor: a task joins the list with its own workspace.
                if (!PathComparer.SameDirectory(entry.WorkspaceRoot, root) || FindLocked(entry.Id) is not null) continue;
                if (!entry.IsFinished)
                {
                    if (OwnerAlive(entry.By)) continue;   // another window's: it is running there
                    var job = JobOf(entry);
                    job.State      = BackgroundTaskState.Cancelled;
                    job.Cause      = TaskEndCause.EditorClosed;
                    job.FinishedAt = entry.Updated ?? job.StartedAt;   // the last moment known of it
                    job.Inherited  = true;
                    _finished.Add(job);
                    stopped.Add(job);
                }
                else
                {
                    // Another window's, still open: not "before the editor closed" — that window did not close.
                    var job = JobOf(entry);
                    job.Inherited = !OwnerAlive(entry.By);
                    _finished.Add(job);
                }
            }
        }
        foreach (var job in stopped) Persist(job);
    }

    /// <summary>Whether the queue that wrote an entry still runs: this process's own, or another process's still alive.</summary>
    private static bool OwnerAlive(TaskJournal.Owner? owner)
    {
        if (owner is null) return false;
        if (owner.Pid == Environment.ProcessId) lock (LiveQueues) return LiveQueues.Contains(owner.Queue);
        return Signals.SignalFile.WriterAlive(owner.Pid, owner.Since);
    }

    /// <summary>Writes <paramref name="job"/> as it stands to its workspace's journal; never throws.</summary>
    private void Persist(Job job)
    {
        if (_journalFor?.Invoke(job.WorkspaceRoot) is not { } journal) return;
        Write(journal, entries =>
        {
            // ⚠ Taken under the journal's lock, never before it: two writes of one task — its end and its report opened —
            // land in either order, and the one that took its copy first would write the older state last.
            TaskJournal.Entry entry;
            lock (_lock) entry = EntryOf(job);
            var at = entries.FindIndex(e => string.Equals(e.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) entries[at] = entry with { Said = entry.Said || entries[at].Said, Read = entry.Read || entries[at].Read };
            else entries.Add(entry);
            // The journal keeps what the list keeps, workspace by workspace: the newest finished tasks, and every one that
            // proposed changes — never the one written now, whose report has not been read yet.
            var finished = entries.Where(e => e.IsFinished && e.Id != entry.Id
                                              && PathComparer.SameDirectory(e.WorkspaceRoot, entry.WorkspaceRoot))
                                  .OrderBy(e => e.FinishedAt ?? e.CreatedAt).ToList();
            foreach (var old in finished.Where(e => e.Proposals is not { Count: > 0 }).Take(Math.Max(0, finished.Count + 1 - MaxFinished)))
                entries.Remove(old);
            return true;
        });
    }

    /// <summary>Changes <paramref name="journal"/>, keeping the path of a copy it had to set aside to say it.</summary>
    private T? Write<T>(TaskJournal journal, Func<List<TaskJournal.Entry>, T> change)
    {
        var result = journal.Update(change);
        if (journal.TakeSetAside() is { } aside) lock (_lock) _setAside.Add(aside);
        return result;
    }

    private TaskJournal.Entry EntryOf(Job job) => new(
        job.Id, job.Objective, job.State, job.CreatedAt, _owner, job.StartedAt, job.FinishedAt, job.Result, job.Error,
        job.Cause, job.Steps.Count > 0 ? [.. job.Steps] : null, job.StepsTaken, job.ProposeWrites,
        job.Proposals.Count > 0 ? [.. job.Proposals.Select(TaskJournal.SavedProposal.Of)] : null,
        job.Context, job.WorkspaceRoot, job.Read, _now(), job.Said);

    private static Job JobOf(TaskJournal.Entry e)
    {
        var job = new Job
        {
            Id = e.Id, Objective = e.Objective, CreatedAt = e.CreatedAt, WorkspaceRoot = e.WorkspaceRoot, Context = e.Context,
            ProposeWrites = e.ProposeWrites, State = e.State, StartedAt = e.StartedAt, FinishedAt = e.FinishedAt,
            Result = e.Result, Error = e.Error, Cause = e.Cause, StepsTaken = e.StepsTaken, Read = e.Read, Said = e.Said,
            Proposals = e.Proposals is { Count: > 0 } saved ? [.. saved.Select(p => p.ToProposal())] : [],
        };
        if (e.Steps is { } steps) job.Steps.AddRange(steps);
        return job;
    }

    // ── Inspection ──────────────────────────────────────────────────────────────

    /// <summary>Every known task — running first, then queued in order, then finished (newest last).</summary>
    internal IReadOnlyList<BackgroundTaskSnapshot> List()
    {
        EnsureLoaded();   // the workspace open now may be one whose journal was not read yet
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
        EnsureLoaded();
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
            // finished a second time into _finished. The drop-branch is
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

        if (dropped is not null)
        {
            Job? droppedJob;
            lock (_lock) droppedJob = FindLocked(id);
            if (droppedJob is not null) Persist(droppedJob);
            RaiseFinished(dropped);
        }
        return true;
    }

    /// <summary>Forgets every finished task — in the journals too. Running and queued ones are untouched.</summary>
    internal int ClearFinished()
    {
        List<Job> forgotten;
        lock (_lock)
        {
            forgotten = [.. _finished];
            _finished.Clear();
        }
        foreach (var root in forgotten.GroupBy(j => j.WorkspaceRoot))
        {
            var ids = root.Select(j => j.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (_journalFor?.Invoke(root.Key) is { } journal) Write(journal, entries => entries.RemoveAll(e => ids.Contains(e.Id)));
        }
        return forgotten.Count;
    }

    // ── Worker ──────────────────────────────────────────────────────────────────

    private async Task WorkerLoopAsync()
    {
        // Started from a gesture it outlives: its runs say their waits through their own steps, not into that gesture's
        // line, which closes under them.
        ModelWaitScope.Leave();
        while (true)
        {
            Job job;
            lock (_lock)
            {
                if (_pending.Count == 0 || _disposed)
                {
                    _worker     = null;   // retire; the next Submit restarts a worker
                    _waitingFor = null;   // nothing waits for the server any more
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
            // Chat first: hold the task at the gate while an interactive turn is in flight — and again after a wait for
            // the server, which can last long enough for a chat turn to start.
            // ⚠ A server that does not answer is waited for, never run into: otherwise each queued task starts, fails at
            // once, and the next one follows — the queue empties itself in failures while the person is away.
            await WaitForChatAndServerAsync(cts.Token).ConfigureAwait(false);

            // ⚠ Its objective is about the workspace it was submitted in. Queued behind another task while Visual Studio
            // opened another solution, it started there — its tools read the root live — and reported on a project it
            // was never asked about, or proposed changes to its files.
            if (WorkspaceLeft(job))
            {
                lock (_lock)
                {
                    FinishLocked(job, BackgroundTaskState.Failed, null, Strings.TaskWorkspaceChanged);
                    return SnapshotLocked(job);
                }
            }

            BackgroundTaskSnapshot started;
            lock (_lock)
            {
                job.State     = BackgroundTaskState.Running;
                job.StartedAt = _now();
                job.Recorder  = job.ProposeWrites ? new ProposalRecorder() : null;
                started       = SnapshotLocked(job);
            }
            Persist(job);

            var outcome = await _runner(started, step => AppendStep(job, step), job.Recorder, cts.Token).ConfigureAwait(false);

            // A run that failed because the server went away says so — and the tasks behind it wait for the server.
            var serverStopped = false;
            if (outcome.Failure is { } failure)
            {
                Diagnostics.Record($"BackgroundTaskQueue({job.Id})", "The task's run failed: " + failure);
                serverStopped = await AskServerAsync(_shutdown.Token).ConfigureAwait(false) is not null;
            }
            lock (_lock)
            {
                job.Proposals = outcome.Proposals;
                if (serverStopped) job.Cause = TaskEndCause.ServerStopped;
                if (outcome.Failure is { } cause) FinishLocked(job, BackgroundTaskState.Failed, null, cause);
                else
                {
                    job.Cause = TaskEndCause.None;   // it finished before the closing reached it
                    FinishLocked(job, BackgroundTaskState.Succeeded, outcome.Report, null);
                }
                return SnapshotLocked(job);
            }
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                // What it had proposed stays to review: stopping a task is not throwing its work away.
                if (job.Recorder is { } recorder) job.Proposals = recorder.Proposals;
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
                if (job.Recorder is { } recorder) job.Proposals = recorder.Proposals;
                FinishLocked(job, BackgroundTaskState.Failed, null, ex.Message);
                return SnapshotLocked(job);
            }
        }
        finally
        {
            lock (_lock) job.Cts = null;
            Persist(job);
        }
    }

    /// <summary>Waits until the chat is idle and the server answers, both at once — saying meanwhile which server the queue
    /// waits for.</summary>
    private async Task WaitForChatAndServerAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await _waitForChatIdle(ct).ConfigureAwait(false);
                if (await AskServerAsync(ct).ConfigureAwait(false) is not { } server) return;
                lock (_lock) _waitingFor = server;
                await _delay(ServerPollInterval, ct).ConfigureAwait(false);
            }
        }
        // Stopped while waiting, the queue no longer says it waits: the next task asks the server again.
        finally { lock (_lock) _waitingFor = null; }
    }

    /// <summary>The server, named, when it does not answer; <c>null</c> when it does or nothing asks it. A question that
    /// fails is no reason to hold the tasks: it is traced, and the server taken as answering.</summary>
    private async Task<string?> AskServerAsync(CancellationToken ct)
    {
        if (_serverDown is null) return null;
        try { return await _serverDown(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("BackgroundTaskQueue.ServerDown", ex);
            return null;
        }
    }

    /// <summary>Whether the workspace open now is no longer the one <paramref name="job"/> was submitted in.</summary>
    private bool WorkspaceLeft(Job job) => WorkspaceLeft(job.WorkspaceRoot);

    /// <summary>Whether a task submitted in <paramref name="root"/> would no longer run in the workspace open now — a retry
    /// is refused at once rather than given a number it spends failing.</summary>
    internal bool WorkspaceLeft(string? root) =>
        _currentRoot is not null && !string.IsNullOrWhiteSpace(root) && !PathComparer.SameDirectory(root, _currentRoot());

    private void AppendStep(Job job, string step)
    {
        if (string.IsNullOrWhiteSpace(step)) return;
        bool persist;
        lock (_lock)
        {
            // A line said again in place — a wait, a summary's progress — replaces the one it continues: one step, never
            // one every beat (StepLines).
            if (job.Steps.Count > 0 && StepLines.SaysAgain(job.Steps[^1], step))
            {
                job.Steps[^1] = step;
                return;
            }
            job.StepsTaken++;
            job.Steps.Add(step);
            // Keep the tail: on a long run the recent steps explain where it got to.
            if (job.Steps.Count > MaxSteps) job.Steps.RemoveRange(0, job.Steps.Count - MaxSteps);
            persist = job.StepsTaken % StepsPerWrite == 0;
        }
        // An editor closed mid-run writes nothing more: what the journal holds of its steps is what was written along.
        if (persist) Persist(job);
    }

    /// <summary>How many steps a running task takes between two writes of its journal entry.</summary>
    private const int StepsPerWrite = 5;

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
        // ⚠ Leaving "current" and entering "finished" is ONE transition, under ONE lock. Split in
        // two — append to _finished here, clear _current in the worker loop afterwards — the job is
        // in both places at once between them, so List() returns it TWICE and Count counts it
        // twice, for a window the caller can neither see nor avoid.
        // ReferenceEquals, not an id comparison: a pending job cancelled before it ever started
        // also finishes here, and it was never _current.
        if (ReferenceEquals(_current, job)) _current = null;
        _finished.Add(job);
        // Never the task that just finished: its report has not been read yet.
        if (_finished.Count > MaxFinished && _finished.FindIndex(0, _finished.Count - 1, j => j.Proposals.Count == 0) is var oldest and >= 0)
            _finished.RemoveAt(oldest);
    }

    private BackgroundTaskSnapshot SnapshotLocked(Job job)
    {
        var position = 0;
        // Waiting for the server, the task the worker holds is the first to go when it answers.
        var waiting  = job.State == BackgroundTaskState.Queued ? _waitingFor : null;
        if (waiting is not null && ReferenceEquals(_current, job)) position = 1;
        else if (job.State == BackgroundTaskState.Queued)
        {
            var rank = waiting is not null && _current is { State: BackgroundTaskState.Queued } ? 2 : 1;
            foreach (var pending in _pending)
            {
                if (ReferenceEquals(pending, job)) { position = rank; break; }
                rank++;
            }
        }

        return new BackgroundTaskSnapshot(
            job.Id, job.Objective, job.State, job.CreatedAt, job.StartedAt, job.FinishedAt,
            job.Result, job.Error, job.Steps.ToArray(), position,
            job.ProposeWrites, job.Proposals, job.StepsTaken, job.Context, job.Cause, job.Read, waiting, job.WorkspaceRoot,
            job.Said, job.Inherited);
    }

    private void RaiseFinished(BackgroundTaskSnapshot snapshot)
    {
        try { TaskFinished?.Invoke(snapshot); }
        catch (Exception ex) { Diagnostics.Swallow("BackgroundTaskQueue.TaskFinished", ex); }
    }

    /// <summary>Cancels everything in flight. Detached runs must not outlive the editor.</summary>
    /// <remarks>Each is written as it stands — the running one with the steps it took: the process may end before its run
    /// unwinds. Left unfinished in the journal with its queue gone, the next start reads it as the closing stopped it — the
    /// same path as an editor killed without closing.</remarks>
    public void Dispose()
    {
        var dropped = new List<BackgroundTaskSnapshot>();
        var write   = new List<Job>();
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            // The worker retires on _disposed: a task still waiting would stay "queued" forever.
            while (_pending.Count > 0)
            {
                var job = _pending.Dequeue();
                job.Cause = TaskEndCause.EditorClosed;
                FinishLocked(job, BackgroundTaskState.Cancelled, null, null);
                dropped.Add(SnapshotLocked(job));
                write.Add(job);
            }
            if (_current is { } running && !IsTerminal(running.State))
            {
                running.Cause = TaskEndCause.EditorClosed;
                write.Add(running);
            }
        }

        // ⚠ Cancelled, never disposed: a worker that dequeued its job just before this still reads
        // _shutdown.Token, and a disposed source throws there — outside its try, faulting the loop
        // with the job stuck in _current. A source without a timer holds nothing to release.
        try { _shutdown.Cancel(); } catch { }

        foreach (var job in write) Persist(job);
        lock (LiveQueues) LiveQueues.Remove(_owner.Queue);
        foreach (var snapshot in dropped) RaiseFinished(snapshot);
    }
}
