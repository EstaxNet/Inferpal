namespace Inferpal.Services.Hardware;

/// <summary>
/// Central priority gate for the single shared Ollama GPU, in the extension-host process.
/// Inferpal talks to one Ollama backend on one GPU; without coordination the continuous
/// embedding workload of background indexing keeps the GPU busy, the chat model can never load,
/// and the chat request starves until it times out.
/// </summary>
/// <remarks>
/// <para>
/// A chat/agent request takes a <see cref="AcquireChatLease"/> for its whole duration (held across the agent loop and
/// tool calls); background embedding loops (RAG index, @Docs index) await <see cref="WaitForChatIdleAsync"/> before each
/// call, and a <c>/task</c> run <see cref="WaitForTurnsToEndAsync"/> before it starts, and pause — without losing progress — until no chat holds the GPU, in this process or in another one of the
/// account (VS and VS Code share it).
/// </para>
/// <para>
/// Static singleton: there is one GPU per process, so no DI is needed. Holding a chat lease also raises the
/// cross-process <see cref="ChatBusySignal"/>, which the ghost text (FIM) and the other processes read, realising the
/// chat &gt; FIM &gt; embedding priority order across the account's processes.
/// </para>
/// <para>
/// ⚠ Two questions, two answers. <b>Is the GPU busy?</b> (<see cref="IsChatActive"/>, what FIM and the embedding loops
/// yield to) and <b>is a turn in flight?</b> (<see cref="IsTurnInFlight"/>, what must not be cut under a turn: its MCP
/// servers restarted, its model unloaded, a <c>/task</c> run started beside it). They differ while a turn waits for a
/// person (an approval card, a step pause, a form): <see cref="PauseThisFlow"/> lets its leases go — the GPU is free, a
/// card left overnight neither silences the completion of every window nor holds every process's indexing — but the
/// turn is still in flight. The flow knows its leases by the tokens it took (an <see cref="AsyncLocal{T}"/> chain),
/// never by a number: a number would let go of another turn's lease.
/// </para>
/// </remarks>
internal static class GpuScheduler
{
    private static readonly object _lock = new();
    /// <summary>The leases held and not paused: the GPU is busy while it is above zero.</summary>
    private static int _chatLeases;
    /// <summary>Every lease held, paused or not: a turn is in flight while one is.</summary>
    private static readonly HashSet<Lease> _live = [];
    private static readonly AsyncLocal<FlowLink?> _flow = new();
    private static TaskCompletionSource<bool> _idleSignal = CompletedSignal();
    private static TaskCompletionSource<bool> _noTurnSignal = CompletedSignal();

    /// <summary>One lease taken in a flow, and the ones taken before it there.</summary>
    private sealed record FlowLink(Lease Lease, FlowLink? Next);

    private static TaskCompletionSource<bool> CompletedSignal()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult(true);
        return tcs;
    }

    /// <summary><c>true</c> while a chat/agent request of this process works on the GPU (a lease not paused).</summary>
    public static bool IsChatActive
    {
        get { lock (_lock) return _chatLeases > 0; }
    }

    /// <summary><c>true</c> while a chat/agent request of this process is in flight, waiting for a person included.</summary>
    public static bool IsTurnInFlight
    {
        get { lock (_lock) return _live.Count > 0; }
    }

    /// <summary>
    /// Marks a chat/agent request as in flight for the lifetime of the returned token. Background
    /// loops pause at their next model call until the (last) lease is disposed. Re-entrant:
    /// overlapping turns are reference-counted. Raises <see cref="ChatBusySignal"/> on the first lease.
    /// </summary>
    public static IDisposable AcquireChatLease()
    {
        // The machine stays awake while the model works (SleepGuard): every chat lease holds it. Taken in the caller's
        // flow, outside this lock: an approval card later in the same flow lets it go while the card waits.
        var awake = SleepGuard.Shared.Hold();
        var lease = new Lease(awake);
        lock (_lock)
        {
            // The chain keeps the leases still held, not every one the flow ever took: a loop that takes one lease per
            // step (a bench, an arena) would grow it without end.
            _flow.Value = new FlowLink(lease, Held(_flow.Value));
            var (busy, live) = (_chatLeases, _live.Count);
            _live.Add(lease);
            _chatLeases++;
            ChangedLocked(busy, live);
        }
        return lease;
    }

    /// <summary>The links of <paramref name="link"/> whose lease is still held, in their order.</summary>
    private static FlowLink? Held(FlowLink? link)
    {
        if (link is null) return null;
        var next = Held(link.Next);
        if (!_live.Contains(link.Lease)) return next;
        return ReferenceEquals(next, link.Next) ? link : link with { Next = next };
    }

    /// <summary>
    /// How long a wait for a person keeps the GPU before letting it go. ⚠ Not at once: most cards are answered in a few
    /// seconds, and the completion or indexing model loaded meanwhile could push the chat model out of a small GPU —
    /// every answered card would then pay a cold reload. The test suite sets it to zero.
    /// </summary>
    internal static TimeSpan PauseGrace { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Lets the GPU go while this flow waits for a person (an approval card, a step pause, a form), as far as this
    /// flow's own live leases go, after <see cref="PauseGrace"/>, and lets the machine sleep with it
    /// (<see cref="SleepGuard.PauseThisFlow"/>); disposed when the person answers, the leases work again. The turn stays
    /// in flight (<see cref="IsTurnInFlight"/>). A no-op for a flow that holds none — a dialog shown outside a turn must
    /// not release another turn's lease.
    /// </summary>
    public static IDisposable PauseThisFlow()
    {
        var asleep = SleepGuard.Shared.PauseThisFlow();
        List<Lease> mine = [];
        lock (_lock)
            for (var link = _flow.Value; link is not null; link = link.Next)
                if (_live.Contains(link.Lease)) mine.Add(link.Lease);
        var token = new PauseToken(mine, asleep);
        if (mine.Count > 0) token.Arm(PauseGrace);
        return token;
    }

    /// <summary>
    /// <c>true</c> when ghost text (FIM) must stay quiet: a chat turn holds the GPU, here or
    /// in another process of the account.
    /// </summary>
    /// <remarks>
    /// Order matters. <see cref="IsChatActive"/> is the <b>local</b> truth - an in-memory counter,
    /// no I/O, exact by construction; <see cref="ChatBusySignal.IsBusy"/> is a best-effort
    /// <b>cross-process</b> channel (writing the marker may fail, and the failure is swallowed by
    /// <c>Diagnostics.Swallow</c>). While the chat lived in the host and FIM in devenv, only the
    /// file could answer. With in-process hosting the two share a process: asking the disk what the
    /// counter already knows would make the chat &gt; FIM priority depend on a write that is
    /// allowed to fail. The file is still consulted afterwards, for the account's other editors,
    /// which share the GPU.
    /// </remarks>
    public static bool ShouldFimYield() => IsChatActive || ChatBusySignal.IsBusy();

    /// <summary>Marker refresh cadence — comfortably under <see cref="ChatBusySignal.MaxAge"/>.</summary>
    private static readonly TimeSpan BusyRefreshPeriod = TimeSpan.FromMinutes(4);
    private static Timer? _busyRefresh;

    /// <summary>Re-stamps this process's busy marker while chat leases are held, so a healthy
    /// long run never trips the staleness fuse. Internal (not private) so tests can exercise the
    /// refresh deterministically instead of waiting out a real timer period.</summary>
    internal static void RefreshBusyMarker()
    {
        lock (_lock)
        {
            if (_live.Count > 0) ChatBusySignal.Write(paused: _chatLeases == 0);
        }
    }

    /// <summary>
    /// After a change of leases: opens or completes the two waits, and keeps the marker saying what this process does —
    /// absent with no turn, "paused" while every turn waits for a person, busy otherwise.
    /// </summary>
    private static void ChangedLocked(int busyBefore, int liveBefore)
    {
        var (busy, live) = (_chatLeases, _live.Count);
        if (busyBefore == 0 && busy > 0 && _idleSignal.Task.IsCompleted)
            _idleSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        else if (busyBefore > 0 && busy == 0)
            _idleSignal.TrySetResult(true);

        if (liveBefore == 0 && live > 0)
        {
            if (_noTurnSignal.Task.IsCompleted)
                _noTurnSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // A single agent run holds one lease for its whole loop, which can outlast the
            // 10-minute staleness fuse of the marker — without a refresh, FIM resumes against
            // the busy GPU mid-run on any healthy long chat.
            _busyRefresh = new Timer(_ => RefreshBusyMarker(), null, BusyRefreshPeriod, BusyRefreshPeriod);
        }
        else if (liveBefore > 0 && live == 0)
        {
            _noTurnSignal.TrySetResult(true);
            _busyRefresh?.Dispose();
            _busyRefresh = null;
            ChatBusySignal.Clear();
            return;
        }

        if (live > 0 && (liveBefore == 0 || (busyBefore == 0) != (busy == 0)))
            ChatBusySignal.Write(paused: busy == 0);
    }

    private static void Release(Lease lease)
    {
        lock (_lock)
        {
            var (busy, live) = (_chatLeases, _live.Count);
            if (!_live.Remove(lease)) return;
            // A lease ended while paused (a Stop on the card) was already not counted as working.
            if (lease.Pauses == 0) _chatLeases--;
            ChangedLocked(busy, live);
        }
    }

    /// <summary>Pauses those of <paramref name="leases"/> still held; returns the ones it paused.</summary>
    private static List<Lease> PauseLocked(List<Lease> leases)
    {
        var (busy, live) = (_chatLeases, _live.Count);
        var paused = leases.Where(_live.Contains).ToList();
        foreach (var lease in paused)
            if (lease.Pauses++ == 0) _chatLeases--;
        ChangedLocked(busy, live);
        return paused;
    }

    private static void ResumeLocked(List<Lease> paused)
    {
        var (busy, live) = (_chatLeases, _live.Count);
        foreach (var lease in paused)
            if (lease.Pauses > 0 && --lease.Pauses == 0 && _live.Contains(lease)) _chatLeases++;
        ChangedLocked(busy, live);
    }

    /// <summary>How often a waiting loop looks again at another process's chat.</summary>
    internal static TimeSpan ElsewherePoll { get; set; } = TimeSpan.FromSeconds(2);

    // The embedding loops call WaitForChatIdleAsync before every chunk: the folder of signals is read at most once per
    // poll period when nothing elsewhere is busy, not tens of thousands of times in a pass.
    private static long _elsewhereIdleAt = -(1L << 62);   // never read: far enough back, and now − it cannot overflow

    private static bool BusyElsewhere()
    {
        var now = Environment.TickCount64;
        // Judged with the period in force now: a period shortened since does not keep an idle answer read under the old.
        if (now - Interlocked.Read(ref _elsewhereIdleAt) < (long)ElsewherePoll.TotalMilliseconds) return false;
        if (ChatBusySignal.IsBusyElsewhere()) return true;
        Interlocked.Exchange(ref _elsewhereIdleAt, now);
        return false;
    }

    /// <summary>
    /// Returns immediately when no chat works on the GPU, in this process or another one of the account; otherwise
    /// waits until none does (or <paramref name="ct"/> fires). Awaited by background embedding loops before each call so
    /// they yield the GPU to interactive work without losing progress. A turn that waits for a person does not hold it.
    /// ⚠ Background only: a request someone waits for (<c>search_codebase</c>, <c>search_docs</c>) is called from inside
    /// a chat and would wait for itself (rule 20).
    /// </summary>
    public static async Task WaitForChatIdleAsync(CancellationToken ct)
    {
        while (true)
        {
            await WaitAsync(() => _idleSignal, () => _chatLeases == 0, ct).ConfigureAwait(false);
            // Another editor's chat on the same GPU: its marker, never this process's own (its leases are known here).
            if (!BusyElsewhere()) return;
            await Task.Delay(ElsewherePoll, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns once no turn is in flight, in this process or another one of the account — a turn waiting for a person
    /// included. For whole runs that must not start beside a turn (<c>/task</c>): started while a card waits, the run
    /// would compete with the turn for the GPU as soon as the person answers.
    /// </summary>
    public static async Task WaitForTurnsToEndAsync(CancellationToken ct)
    {
        while (true)
        {
            await WaitForLocalTurnsToEndAsync(ct).ConfigureAwait(false);
            if (!ChatBusySignal.IsTurnInFlightElsewhere()) return;
            await Task.Delay(ElsewherePoll, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns once no turn of THIS process is in flight, a turn waiting for a person included. For a gesture that must
    /// not cut a turn of this process (its MCP servers restarted under its tools), not for GPU work.
    /// </summary>
    public static Task WaitForLocalTurnsToEndAsync(CancellationToken ct) =>
        WaitAsync(() => _noTurnSignal, () => _live.Count == 0, ct);

    /// <summary>Waits for <paramref name="signal"/>'s current instance until <paramref name="done"/> holds.</summary>
    private static async Task WaitAsync(Func<TaskCompletionSource<bool>> signal, Func<bool> done, CancellationToken ct)
    {
        while (true)
        {
            Task idle;
            lock (_lock)
            {
                if (done()) return;
                idle = signal().Task; // captured under lock — a release completes this instance
            }

            // Race the signal against cancellation so a cancelled loop never hangs here.
            var cancelTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(static s => ((TaskCompletionSource<bool>)s!).TrySetResult(true), cancelTcs))
                await Task.WhenAny(idle, cancelTcs.Task).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
        }
    }

    private sealed class Lease(IDisposable awake) : IDisposable
    {
        private int _disposed;

        /// <summary>How many waits for a person in its flow hold it paused (under the scheduler's lock).</summary>
        public int Pauses;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Release(this);
            awake.Dispose();
        }
    }

    /// <summary>One wait for a person: its leases, paused once its grace is over, until it is disposed.</summary>
    private sealed class PauseToken(List<Lease> leases, IDisposable asleep) : IDisposable
    {
        private bool _disposed;           // under the scheduler's lock, like the two below
        private List<Lease>? _paused;
        private Timer? _grace;

        public void Arm(TimeSpan grace)
        {
            if (grace <= TimeSpan.Zero) { Pause(); return; }
            lock (_lock) if (!_disposed) _grace = new Timer(_ => Pause(), null, grace, Timeout.InfiniteTimeSpan);
        }

        private void Pause()
        {
            lock (_lock)
                if (!_disposed && _paused is null) _paused = PauseLocked(leases);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _grace?.Dispose();
                if (_paused is not null) ResumeLocked(_paused);
            }
            asleep.Dispose();
        }
    }
}
