namespace Inferpal.Services.Hardware;

/// <summary>
/// The one place where a gesture that rewrites what a turn reads — the model server, above all — asks whether a turn of
/// this process runs, and waits for it to end.
/// </summary>
/// <remarks>
/// ⚠ A model lease alone does not say "a turn runs": it is taken per model call, for the whole run only by the agent loop,
/// so a long command (<c>/tdd</c>, <c>/fix-build</c>, Smart Fix, <c>/test coverage</c>) has gaps without one. The editor
/// holds the door for each turn it owns, from start to end (<see cref="Hold"/>); a lease (<see cref="GpuScheduler.IsTurnInFlight"/>:
/// a <c>/task</c> run, a card waiting) counts too. The door says busy; the site decides to wait or to refuse.
/// </remarks>
internal static class TurnDoor
{
    private static readonly object Gate = new();
    private static int _held;
    private static TaskCompletionSource<bool> _released = Released();

    private static TaskCompletionSource<bool> Released()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.TrySetResult(true);
        return tcs;
    }

    /// <summary>A turn of this process runs: held by its editor, or a model lease in flight.</summary>
    public static bool Busy
    {
        get { lock (Gate) if (_held > 0) return true; return GpuScheduler.IsTurnInFlight; }
    }

    /// <summary>Held by the editor for a turn it owns, from start to end; released by disposing.</summary>
    public static IDisposable Hold()
    {
        lock (Gate)
        {
            if (_held++ == 0) _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        return new Release();
    }

    private sealed class Release : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            TaskCompletionSource<bool>? released = null;
            lock (Gate)
            {
                if (--_held == 0) released = _released;
            }
            released?.TrySetResult(true);
        }
    }

    /// <summary>
    /// Runs <paramref name="act"/> now if no turn of this process runs, and says whether it did. ⚠ Judged and run under the
    /// door's lock, which <see cref="Hold"/> takes too: a turn starting between the judgement and the act would otherwise
    /// see its server change under its first call. Keep <paramref name="act"/> short and synchronous.
    /// </summary>
    public static bool TryNow(Action act)
    {
        lock (Gate)
        {
            if (_held > 0 || GpuScheduler.IsTurnInFlight) return false;
            act();
            return true;
        }
    }

    /// <summary>Runs <paramref name="act"/> once no turn of this process runs — at once when none does
    /// (<see cref="TryNow"/>).</summary>
    public static async Task WhenIdleAsync(Action act, CancellationToken ct = default)
    {
        while (!TryNow(act))
        {
            Task held;
            lock (Gate) held = _released.Task;
            await held.WaitAsync(ct).ConfigureAwait(false);
            await GpuScheduler.WaitForLocalTurnsToEndAsync(ct).ConfigureAwait(false);
        }
    }
}
