namespace Inferpal.Services;

/// <summary>
/// Central priority gate for the single shared Ollama GPU, in the extension-host process.
/// Inferpal talks to one Ollama backend on one GPU; without coordination the continuous
/// embedding workload of background indexing keeps the GPU busy, the chat model can never load,
/// and the chat request starves until it times out.
/// </summary>
/// <remarks>
/// <para>
/// Generalises the ad-hoc gate that previously lived in <c>ProjectIndexService</c>. A chat/agent
/// request takes a <see cref="AcquireChatLease"/> for its whole duration (held across the agent
/// loop and tool calls); background loops (RAG index, @Docs index) await
/// <see cref="WaitForChatIdleAsync"/> before each embedding and pause — without losing progress —
/// until the last chat lease ends.
/// </para>
/// <para>
/// Static singleton: there is one GPU per process, so no DI is needed. Holding a chat lease also
/// raises the cross-process <see cref="ChatBusySignal"/> so the in-devenv ghost-text (FIM) yields,
/// realising the chat &gt; FIM &gt; embedding priority order.
/// </para>
/// </remarks>
internal static class GpuScheduler
{
    private static readonly object _lock = new();
    private static int _chatLeases;
    private static TaskCompletionSource<bool> _idleSignal = CompletedSignal();

    private static TaskCompletionSource<bool> CompletedSignal()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult(true);
        return tcs;
    }

    /// <summary><c>true</c> while at least one chat/agent request holds a lease.</summary>
    public static bool IsChatActive
    {
        get { lock (_lock) return _chatLeases > 0; }
    }

    /// <summary>
    /// Marks a chat/agent request as in flight for the lifetime of the returned token. Background
    /// loops pause at their next embedding boundary until the (last) lease is disposed. Re-entrant:
    /// overlapping turns are reference-counted. Raises <see cref="ChatBusySignal"/> on the first lease.
    /// </summary>
    public static IDisposable AcquireChatLease()
    {
        lock (_lock)
        {
            if (++_chatLeases == 1)
            {
                if (_idleSignal.Task.IsCompleted)
                    _idleSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                ChatBusySignal.Write();
            }
        }
        return new Lease();
    }

    private static void Release()
    {
        lock (_lock)
        {
            if (_chatLeases > 0 && --_chatLeases == 0)
            {
                _idleSignal.TrySetResult(true);
                ChatBusySignal.Clear();
            }
        }
    }

    /// <summary>
    /// Returns immediately when no chat lease is active; otherwise blocks until the last one is
    /// released (or <paramref name="ct"/> fires). Awaited by background embedding loops before
    /// each call so they yield the GPU to interactive work without losing progress.
    /// </summary>
    public static async Task WaitForChatIdleAsync(CancellationToken ct)
    {
        while (true)
        {
            Task idle;
            lock (_lock)
            {
                if (_chatLeases == 0) return;
                idle = _idleSignal.Task; // captured under lock — Release completes this instance
            }

            // Race the idle signal against cancellation so a cancelled loop never hangs here.
            var cancelTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(static s => ((TaskCompletionSource<bool>)s!).TrySetResult(true), cancelTcs))
                await Task.WhenAny(idle, cancelTcs.Task).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
        }
    }

    private sealed class Lease : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Release();
        }
    }
}
