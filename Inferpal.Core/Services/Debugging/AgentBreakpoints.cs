namespace Inferpal.Services.Debugging;

/// <summary>The breakpoints the assistant set — the only ones it may remove.</summary>
/// <remarks>
/// ⚠ A breakpoint lives in the editor's own list, saved with the workspace, whoever set it: left there, the user's
/// next debugging session stops on lines they never chose. And "clear the breakpoint at this line" removed whatever
/// was there — the user's own, its condition included. So what the assistant adds is recorded here, removed when its
/// session stops, and a breakpoint it did not add is never removed by it.
/// </remarks>
internal sealed class AgentBreakpoints
{
    private readonly object _gate = new();
    private readonly HashSet<(string File, int Line)> _owned = new(new LocationComparer());

    public void Track(string file, int line)   { lock (_gate) _owned.Add((file, line)); }
    public void Untrack(string file, int line) { lock (_gate) _owned.Remove((file, line)); }
    public bool Owns(string file, int line)    { lock (_gate) return _owned.Contains((file, line)); }

    public IReadOnlyList<(string File, int Line)> Snapshot()
    {
        lock (_gate) return [.. _owned];
    }

    /// <summary>Removes every breakpoint the assistant set that is still there; returns how many went.</summary>
    public async Task<int> RemoveAllAsync(IDebugSession session, CancellationToken ct)
    {
        var removed = 0;
        foreach (var (file, line) in Snapshot())
        {
            try
            {
                if (await session.RemoveBreakpointAsync(file, line, ct)) removed++;
            }
            catch (OperationCanceledException) { throw; }
            catch (DebuggerNotAnsweringException ex)
            {
                // Still there, as far as anyone knows: kept, so the next stop of the session removes it.
                Diagnostics.Swallow("AgentBreakpoints.RemoveAll", ex);
                continue;
            }
            catch (Exception ex) { Diagnostics.Swallow("AgentBreakpoints.RemoveAll", ex); }
            Untrack(file, line);
        }
        return removed;
    }

    /// <summary>Same file (by the file system's own rule) and same line.</summary>
    internal static bool Same(string fileA, int lineA, string fileB, int lineB) =>
        lineA == lineB && PathComparer.Default.Equals(fileA, fileB);

    private sealed class LocationComparer : IEqualityComparer<(string File, int Line)>
    {
        public bool Equals((string File, int Line) a, (string File, int Line) b) => Same(a.File, a.Line, b.File, b.Line);
        public int GetHashCode((string File, int Line) l) => HashCode.Combine(PathComparer.Default.GetHashCode(l.File), l.Line);
    }
}

/// <summary>
/// The front-end's debugger, with one difference: stopping it first removes the breakpoints the assistant set — through
/// the tool's <c>stop</c> and through <c>/debug stop</c> alike, since both reach the session through the tool registry.
/// </summary>
internal sealed class AgentCleaningDebugSession(IDebugSession inner, AgentBreakpoints agent) : IDebugSession
{
    public AgentBreakpoints Agent => agent;

    public bool IsAvailable => inner.IsAvailable;
    public Task<DebugBreakpointInfo?> AddBreakpointAsync(string file, int line, CancellationToken ct) => inner.AddBreakpointAsync(file, line, ct);
    public Task<bool> RemoveBreakpointAsync(string file, int line, CancellationToken ct) => inner.RemoveBreakpointAsync(file, line, ct);
    public Task<IReadOnlyList<DebugBreakpointInfo>> ListBreakpointsAsync(CancellationToken ct) => inner.ListBreakpointsAsync(ct);
    /// <summary>
    /// ⚠ A program that runs to completion ENDS the session as surely as a stop does — and it is the ordinary outcome of
    /// a breakpoint on a line the run never reaches. Cleaned only by <see cref="StopAsync"/>, the agent's breakpoints
    /// stayed in the editor's saved list (the model has no reason to stop a program that already ended), and the user's
    /// next F5 stopped on a line they never chose.
    /// </summary>
    public async Task<DebugStartResult> StartAsync(CancellationToken ct)
    {
        var result = await inner.StartAsync(ct);
        // Only a run that ENDED: a program still running keeps its session, and the breakpoints with it.
        if (result is { State: null, Failure: null, StillRunning: false }) await agent.RemoveAllAsync(inner, ct);
        return result;
    }
    /// <summary>
    /// ⚠ A run that ends on a resume ends the session too — the commonest end of an agent's session: a <c>continue</c>
    /// after its last inspection. Only an ENDED run: one still running keeps its breakpoints.
    /// </summary>
    public async Task<DebugResumeResult> ContinueAsync(CancellationToken ct) => await CleanedAfter(await inner.ContinueAsync(ct), ct);
    public async Task<DebugResumeResult> StepAsync(DebugStepKind kind, CancellationToken ct) => await CleanedAfter(await inner.StepAsync(kind, ct), ct);

    private async Task<DebugResumeResult> CleanedAfter(DebugResumeResult result, CancellationToken ct)
    {
        if (result.Outcome == DebugResumeOutcome.Ended) await agent.RemoveAllAsync(inner, ct);
        return result;
    }
    public Task<DebugStopState?> GetStateAsync(CancellationToken ct) => inner.GetStateAsync(ct);
    public Task<string?> EvaluateAsync(string expression, int? frameId, CancellationToken ct) => inner.EvaluateAsync(expression, frameId, ct);

    public async Task StopAsync(CancellationToken ct)
    {
        await agent.RemoveAllAsync(inner, ct);
        await inner.StopAsync(ct);
    }
}
