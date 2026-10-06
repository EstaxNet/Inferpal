using Inferpal.Services.Signals;

namespace Inferpal.Services.Debugging;

/// <summary>
/// <see cref="IDebugSession"/> for the Visual Studio front-end: turns each port call into a
/// <see cref="DebugCommandSignal"/> request served by the in-process driver inside devenv.
/// </summary>
/// <remarks>
/// <para>
/// The out-of-process extension host cannot touch <c>EnvDTE</c>, and the out-of-process debugger API
/// is not the channel either — while the MEF package already holds a real <c>DTE</c> and already
/// publishes break snapshots the other way (<see cref="DebuggerStateSignal"/>). This class is the
/// reverse leg of that same transport.
/// </para>
/// <para>
/// <b>One request at a time, enforced here.</b> A debugger is a single stateful machine: two
/// overlapping <c>continue</c> calls would race for the same stop event, and the second answer would
/// describe a state the first caller believes it caused. The semaphore is the reason
/// <see cref="DebugCommandSignal"/> can be a single pair of files instead of a queue.
/// </para>
/// <para>
/// An ordinary debugger answer (no breakpoint there, nothing paused) is <c>null</c>/<c>false</c>, as the port requires;
/// a driver that did not ANSWER — held by a dialog, gone, or failing the request — is not one, and throws
/// <see cref="DebuggerNotAnsweringException"/> (a start and a resume say it in their result instead).
/// </para>
/// </remarks>
internal sealed class SignalDebugSession : IDebugSession
{
    // Operation names on the wire. Kept as constants because the driver lives in another
    // assembly *and* another process: a typo here is a runtime no-op, not a compile error.
    // The literals live in <see cref="DebugOps"/>, which the in-process driver (net472) compiles
    // from the SAME source file - see Inferpal.InProc.csproj.
    internal const string OpAddBreakpoint    = DebugOps.AddBreakpoint;
    internal const string OpRemoveBreakpoint = DebugOps.RemoveBreakpoint;
    internal const string OpListBreakpoints  = DebugOps.ListBreakpoints;
    internal const string OpStart            = DebugOps.Start;
    internal const string OpContinue         = DebugOps.Continue;
    internal const string OpStepOver         = DebugOps.StepOver;
    internal const string OpStepInto         = DebugOps.StepInto;
    internal const string OpStepOut          = DebugOps.StepOut;
    internal const string OpState            = DebugOps.State;
    internal const string OpEvaluate         = DebugOps.Evaluate;
    internal const string OpStop             = DebugOps.Stop;
    /// <summary>Attaches to a waiting repro runner and captures the unhandled-exception stop.</summary>
    internal const string OpCaptureTest      = DebugOps.CaptureTest;

    /// <summary>
    /// Starting a session builds the solution first, so its budget is measured in minutes — unlike
    /// every other operation here, which answers in well under a second on an already-built
    /// solution.
    /// </summary>
    internal static TimeSpan StartTimeout { get; set; } = DebugOps.StartBudget;

    /// <summary>Resuming or stepping waits for the next stop, which is user code running.</summary>
    internal static TimeSpan ResumeTimeout { get; set; } = DebugOps.ResumeBudget;

    /// <summary>Everything else is a question asked of a debugger that is already paused.</summary>
    internal static TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(20);

    // Shared with the capture client: the channel carries one request at a time, whoever asks.
    private static SemaphoreSlim _oneAtATime => Signals.DebugCommandSignal.ChannelLock;

    public bool IsAvailable => DebugCommandSignal.IsDriverReady();

    public async Task<DebugBreakpointInfo?> AddBreakpointAsync(string file, int line, CancellationToken ct)
    {
        var response = await AskAsync(
            new(Id: string.Empty, Pid: 0, Ts: 0, Op: OpAddBreakpoint, File: file, Line: line),
            QueryTimeout, ct);
        // The driver returns the breakpoint as the debugger bound it, which may differ from what
        // was asked (VS moves a breakpoint to the next executable line). None bound = refused.
        return response.Breakpoints?.FirstOrDefault();
    }

    public async Task<bool> RemoveBreakpointAsync(string file, int line, CancellationToken ct)
    {
        var response = await AskAsync(
            new(Id: string.Empty, Pid: 0, Ts: 0, Op: OpRemoveBreakpoint, File: file, Line: line),
            QueryTimeout, ct);
        return response.Flag;
    }

    public async Task<IReadOnlyList<DebugBreakpointInfo>> ListBreakpointsAsync(CancellationToken ct)
    {
        var response = await AskAsync(
            new(Id: string.Empty, Pid: 0, Ts: 0, Op: OpListBreakpoints), QueryTimeout, ct);
        return response.Breakpoints ?? [];
    }

    public async Task<DebugStartResult> StartAsync(CancellationToken ct)
    {
        // Checked here rather than inferred from a null answer below: the driver going away and the
        // launch never reacting are different facts, and they deserve different sentences.
        if (!IsAvailable)
            return DebugStartResult.Failed("No debugger is reachable from this editor session.");

        var (response, _) = await SendAsync(new(Id: string.Empty, Pid: 0, Ts: 0, Op: OpStart), StartTimeout, ct);

        // No answer at all within the start budget. The driver answers ahead of this budget
        // (DebugOps.AnswerMargin) — a failed build, a launch stuck in design mode, a program still
        // running — so silence means the driver itself is held, most often by a dialog on the UI
        // thread. Never "ran to completion": say what is actually known.
        if (response is null)
            return DebugStartResult.Failed(
                $"No answer from the debugger within {Humanize(StartTimeout)}: the launch did not start, most "
              + "likely because a dialog is waiting in Visual Studio — look at the IDE before assuming "
              + "anything about the program's behaviour.");

        // The driver's own words, unchanged. The commonest one is a build that failed: the driver
        // refuses the launch itself rather than letting Visual Studio raise its modal.
        if (!response.Ok)
            return DebugStartResult.Failed(response.Error ?? "The debugger refused to start the session.");

        // Flag on a start = the program is still running, no stop within the budget (VsDebugDriver).
        return response.State is { } state ? DebugStartResult.Stopped(state)
             : response.Flag               ? DebugStartResult.NoStopYet
                                           : DebugStartResult.RanToCompletion;
    }

    public Task<DebugResumeResult> ContinueAsync(CancellationToken ct) => ResumeAsync(OpContinue, ct);

    public Task<DebugResumeResult> StepAsync(DebugStepKind kind, CancellationToken ct) =>
        ResumeAsync(kind switch
        {
            DebugStepKind.Into => OpStepInto,
            DebugStepKind.Out  => OpStepOut,
            _                  => OpStepOver,
        }, ct);

    public async Task<DebugStopState?> GetStateAsync(CancellationToken ct) =>
        // No state = not paused (running, ended, or never started): an ordinary answer.
        (await AskAsync(new(Id: string.Empty, Pid: 0, Ts: 0, Op: OpState), QueryTimeout, ct)).State;

    public async Task<string?> EvaluateAsync(string expression, int? frameId, CancellationToken ct)
    {
        var (response, silence) = await SendAsync(
            new(Id: string.Empty, Pid: 0, Ts: 0, Op: OpEvaluate, Expression: expression, FrameId: frameId),
            QueryTimeout, ct);
        if (response is null) throw new DebuggerNotAnsweringException(silence!);
        // A refused evaluation is the ordinary answer to an invalid expression.
        return response.Ok ? response.Text : null;
    }

    public async Task StopAsync(CancellationToken ct) =>
        await AskAsync(new(Id: string.Empty, Pid: 0, Ts: 0, Op: OpStop), QueryTimeout, ct);

    /// <summary>
    /// The budget as a sentence. Rounding a 20-second wait to "0 minutes" would read as a bug in
    /// the message itself, and this text is the only thing the agent gets to reason about.
    /// </summary>
    private static string Humanize(TimeSpan budget) =>
        budget < TimeSpan.FromMinutes(1)
            ? $"{budget.TotalSeconds:0} second(s)"
            : $"{budget.TotalMinutes:0} minute(s)";

    /// <summary>A resume, and what it came to — read from the driver's answer (<see cref="DebugOps"/>'s markers).</summary>
    private async Task<DebugResumeResult> ResumeAsync(string op, CancellationToken ct)
    {
        var (response, silence) = await SendAsync(new(Id: string.Empty, Pid: 0, Ts: 0, Op: op), ResumeTimeout, ct);
        if (response is null) return DebugResumeResult.Failed(silence!);
        if (!response.Ok) return DebugResumeResult.Failed(response.Error ?? "The debugger refused to resume.");
        if (response.State is { } state) return DebugResumeResult.Stopped(state);
        return response.Text switch
        {
            DebugOps.Resumed.NotPaused    => DebugResumeResult.NotPaused,
            DebugOps.Resumed.StillRunning => DebugResumeResult.StillRunning,
            _                           => DebugResumeResult.Ended,
        };
    }

    /// <summary>
    /// A question the driver must answer: its answer, or <see cref="DebuggerNotAnsweringException"/> when it did not —
    /// no answer at all, or the request failed on its side.
    /// </summary>
    private async Task<DebugCommandResponse> AskAsync(DebugCommandRequest template, TimeSpan timeout, CancellationToken ct)
    {
        var (response, silence) = await SendAsync(template, timeout, ct);
        if (response is null) throw new DebuggerNotAnsweringException(silence!);
        if (!response.Ok)
            throw new DebuggerNotAnsweringException(
                "The debugger did not answer the request: " + (response.Error ?? "it failed without saying why") + ".");
        return response;
    }

    /// <summary>
    /// Stamps the request with this process's identity and clock, publishes it, and waits for the
    /// answer that carries its id. Serialised against every other call. No answer comes with the reason it did not
    /// (<c>Silence</c>), in the model's words.
    /// </summary>
    private async Task<(DebugCommandResponse? Response, string? Silence)> SendAsync(
        DebugCommandRequest template, TimeSpan timeout, CancellationToken ct)
    {
        if (!DebugCommandSignal.IsDriverReady())
            return (null, "The debugger did not answer: Inferpal's in-process half is not running in Visual Studio, "
                        + "so nothing can reach its debugger.");

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
            if (id is null) return (null, "The debugger did not answer: the request could not be handed to Visual Studio.");

            // WaitForAnswerAsync withdraws the request itself on every path that produces no
            // answer — timeout, driver gone, AND cancellation. Withdrawn on the timeout branch
            // only, a cancelled turn leaves a live request behind, and a driver waking up later
            // starts the user's program long after the agent gave up.
            var answer = await DebugCommandSignal.WaitForAnswerAsync(id, timeout, ct);
            return answer is not null
                ? (answer, null)
                : (null, $"The debugger did not answer within {Humanize(timeout)}: a dialog is probably waiting in Visual "
                       + "Studio. Nothing is known about the program until it answers — look at the IDE first.");
        }
        finally
        {
            _oneAtATime.Release();
        }
    }
}
