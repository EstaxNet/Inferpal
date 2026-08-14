using Inferpal.Services.Signals;

namespace Inferpal.Services.Debugging;

/// <summary>
/// <see cref="IDebugSession"/> for the Visual Studio front-end: turns each port call into a
/// <see cref="DebugCommandSignal"/> request served by the in-process driver inside devenv.
/// </summary>
/// <remarks>
/// <para>
/// The out-of-process extension host cannot touch <c>EnvDTE</c>, and the roadmap §21 probe
/// established that the out-of-process debugger API is not the channel — the MEF package already
/// holds a real <c>DTE</c> and already publishes break snapshots the other way
/// (<see cref="DebuggerStateSignal"/>). This class is the reverse leg of that same transport.
/// </para>
/// <para>
/// <b>One request at a time, enforced here.</b> A debugger is a single stateful machine: two
/// overlapping <c>continue</c> calls would race for the same stop event, and the second answer
/// would describe a state the first caller believes it caused. The semaphore is the reason
/// <see cref="DebugCommandSignal"/> can be a single pair of files instead of a queue.
/// </para>
/// <para>
/// Every method is best-effort as the port requires: a timeout, a dead driver or an I/O failure
/// yields <c>null</c>/<c>false</c>, which the tools render as a plain sentence.
/// <see cref="OperationCanceledException"/> is the only exception that propagates.
/// </para>
/// </remarks>
internal sealed class SignalDebugSession : IDebugSession
{
    // Operation names on the wire. Kept as constants because the driver lives in another
    // assembly *and* another process: a typo here is a runtime no-op, not a compile error.
    internal const string OpAddBreakpoint    = "add_bp";
    internal const string OpRemoveBreakpoint = "remove_bp";
    internal const string OpListBreakpoints  = "list_bp";
    internal const string OpStart            = "start";
    internal const string OpContinue         = "continue";
    internal const string OpStepOver         = "step_over";
    internal const string OpStepInto         = "step_into";
    internal const string OpStepOut          = "step_out";
    internal const string OpState            = "state";
    internal const string OpEvaluate         = "evaluate";
    internal const string OpStop             = "stop";

    /// <summary>
    /// Starting a session builds the solution first, so its budget is measured in minutes. This is
    /// the one place where the probe's numbers do not apply: it measured 1,5 s to the first break
    /// on an <i>already built</i> throwaway solution, and said so.
    /// </summary>
    internal static TimeSpan StartTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Resuming or stepping waits for the next stop, which is user code running.</summary>
    internal static TimeSpan ResumeTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Everything else is a question asked of a debugger that is already paused.</summary>
    internal static TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public bool IsAvailable => DebugCommandSignal.IsDriverReady();

    public async Task<DebugBreakpointInfo?> AddBreakpointAsync(string file, int line, CancellationToken ct)
    {
        var response = await SendAsync(
            new(Id: string.Empty, Pid: 0, Ts: 0, Op: OpAddBreakpoint, File: file, Line: line),
            QueryTimeout, ct);
        // The driver returns the breakpoint as the debugger bound it, which may differ from what
        // was asked (VS moves a breakpoint to the next executable line).
        return response is { Ok: true } ? response.Breakpoints?.FirstOrDefault() : null;
    }

    public async Task<bool> RemoveBreakpointAsync(string file, int line, CancellationToken ct)
    {
        var response = await SendAsync(
            new(Id: string.Empty, Pid: 0, Ts: 0, Op: OpRemoveBreakpoint, File: file, Line: line),
            QueryTimeout, ct);
        return response is { Ok: true, Flag: true };
    }

    public async Task<IReadOnlyList<DebugBreakpointInfo>> ListBreakpointsAsync(CancellationToken ct)
    {
        var response = await SendAsync(
            new(Id: string.Empty, Pid: 0, Ts: 0, Op: OpListBreakpoints), QueryTimeout, ct);
        return response is { Ok: true, Breakpoints: { } bps } ? bps : [];
    }

    public async Task<DebugStartResult> StartAsync(CancellationToken ct)
    {
        // Checked here rather than inferred from a null answer below: the driver going away and the
        // launch never reacting are different facts, and they deserve different sentences.
        if (!IsAvailable)
            return DebugStartResult.Failed("No debugger is reachable from this editor session.");

        var response = await SendAsync(new(Id: string.Empty, Pid: 0, Ts: 0, Op: OpStart), StartTimeout, ct);

        // No answer at all within the start budget. The dominant cause is a launch that never left
        // design mode — a failed build, or the "there were build errors" dialog waiting on a human —
        // and the tranche-2 review flagged reporting that as "ran to completion" as the one place
        // where this feature would lie to the agent. Say what is actually known instead.
        if (response is null)
            return DebugStartResult.Failed(
                $"The debugger did not start within {Humanize(StartTimeout)}. The most likely cause is "
              + "a build that failed or a dialog waiting in the IDE — check the Build output before "
              + "assuming anything about the program's behaviour.");

        // The driver's own words, unchanged. Since 2026-08-06 the commonest one is a build that
        // failed: it refuses the launch itself rather than letting Visual Studio raise its modal.
        if (!response.Ok)
            return DebugStartResult.Failed(response.Error ?? "The debugger refused to start the session.");

        return response.State is { } state ? DebugStartResult.Stopped(state) : DebugStartResult.RanToCompletion;
    }

    public Task<DebugStopState?> ContinueAsync(CancellationToken ct) =>
        StopStateAsync(OpContinue, ResumeTimeout, ct);

    public Task<DebugStopState?> StepAsync(DebugStepKind kind, CancellationToken ct) =>
        StopStateAsync(kind switch
        {
            DebugStepKind.Into => OpStepInto,
            DebugStepKind.Out  => OpStepOut,
            _                  => OpStepOver,
        }, ResumeTimeout, ct);

    public Task<DebugStopState?> GetStateAsync(CancellationToken ct) =>
        StopStateAsync(OpState, QueryTimeout, ct);

    public async Task<string?> EvaluateAsync(string expression, int? frameId, CancellationToken ct)
    {
        var response = await SendAsync(
            new(Id: string.Empty, Pid: 0, Ts: 0, Op: OpEvaluate, Expression: expression, FrameId: frameId),
            QueryTimeout, ct);
        return response is { Ok: true } ? response.Text : null;
    }

    public async Task StopAsync(CancellationToken ct) =>
        await SendAsync(new(Id: string.Empty, Pid: 0, Ts: 0, Op: OpStop), QueryTimeout, ct);

    /// <summary>
    /// The budget as a sentence. Rounding a 20-second wait to "0 minutes" would read as a bug in
    /// the message itself, and this text is the only thing the agent gets to reason about.
    /// </summary>
    private static string Humanize(TimeSpan budget) =>
        budget < TimeSpan.FromMinutes(1)
            ? $"{budget.TotalSeconds:0} second(s)"
            : $"{budget.TotalMinutes:0} minute(s)";

    private async Task<DebugStopState?> StopStateAsync(string op, TimeSpan timeout, CancellationToken ct)
    {
        var response = await SendAsync(new(Id: string.Empty, Pid: 0, Ts: 0, Op: op), timeout, ct);
        // A successful call with no state means the program is not paused — it ran to completion,
        // or it was never started. That is an ordinary answer, so it is `null`, not an error.
        return response is { Ok: true } ? response.State : null;
    }

    /// <summary>
    /// Stamps the request with this process's identity and clock, publishes it, and waits for the
    /// answer that carries its id. Serialised against every other call.
    /// </summary>
    private async Task<DebugCommandResponse?> SendAsync(
        DebugCommandRequest template, TimeSpan timeout, CancellationToken ct)
    {
        if (!DebugCommandSignal.IsDriverReady()) return null;

        await _oneAtATime.WaitAsync(ct);
        try
        {
            var request = template with
            {
                Id  = Guid.NewGuid().ToString("N"),
                Pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                Ts  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            var id = DebugCommandSignal.WriteRequest(request);
            if (id is null) return null;

            var response = await DebugCommandSignal.WaitForResponseAsync(id, timeout, ct);
            if (response is null)
                // Nobody answered in time. Withdraw the request so a driver that wakes up later
                // cannot start the user's program long after the agent gave up on it.
                DebugCommandSignal.DiscardRequest();

            return response;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }
}
