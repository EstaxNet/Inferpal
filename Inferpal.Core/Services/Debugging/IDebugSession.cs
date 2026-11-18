namespace Inferpal.Services.Debugging;

/// <summary>How a stepping request advances execution.</summary>
internal enum DebugStepKind
{
    /// <summary>Run the current line, without descending into calls.</summary>
    Over,
    /// <summary>Descend into the call on the current line.</summary>
    Into,
    /// <summary>Run to the return of the current frame.</summary>
    Out,
}

/// <summary>One frame of the call stack, top of stack first.</summary>
/// <param name="Id">Adapter-assigned frame handle, required to scope an evaluation.</param>
internal sealed record DebugFrame(int Id, string Function, string? File, int? Line);

/// <summary>
/// One variable of a frame.
/// </summary>
/// <remarks>
/// ⚠ <paramref name="Value"/> and <paramref name="Type"/> are <b>opaque</b>: they are rendered by
/// the debug adapter, not by us, and the two front-ends disagree on purpose — the same list of
/// strings shows as <c>"probe-42"</c> / <c>Count = 3</c> under Visual Studio and <c>'probe-42'</c> /
/// <c>(3) [21, 42, 43]</c> under VS Code. Present them, never parse them, never compare them across
/// editors: matching a tool's own message is a mistake this repository has made before.
/// </remarks>
internal sealed record DebugVariable(string Name, string Type, string Value);

/// <summary>A breakpoint as the debugger knows it — its condition, hit count and tracepoint message as the debugger holds
/// them, <c>null</c> for none (or a debugger that does not say).</summary>
internal sealed record DebugBreakpointInfo(string File, int Line, bool Enabled, string? Condition = null, int? HitCount = null,
                                           string? Log = null);

/// <summary>
/// What a breakpoint the assistant asks for carries beyond its place: a condition, the pass it stops at, or a message
/// written at each pass without stopping (a tracepoint; a logpoint in VS Code).
/// </summary>
/// <remarks>
/// ⚠ A tracepoint's message is written with <see cref="TraceMarker"/> in front, and the trace is read back by that mark
/// only (<see cref="TraceLines"/>): the debug output also holds the program's own output and the debugger's (modules
/// loaded, threads ended) — returned whole, the trace drowns, and the program's output passes for what was asked.
/// </remarks>
internal sealed record BreakpointOptions(string? Condition = null, int? HitCount = null, string? Log = null)
{
    /// <summary>The mark in front of every line a tracepoint writes.</summary>
    internal const string TraceMarker = "[inferpal] ";

    /// <summary>Nothing beyond the place: a plain breakpoint.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Condition) && HitCount is null && string.IsNullOrWhiteSpace(Log);

    /// <summary>The message as the debugger is asked to write it: marked; <c>null</c> for a breakpoint that stops.</summary>
    public string? MarkedLog => string.IsNullOrWhiteSpace(Log) ? null : TraceMarker + Log!.Trim();

    /// <summary>The condition as the debugger is asked to test it; <c>null</c> for none.</summary>
    public string? TrimmedCondition => string.IsNullOrWhiteSpace(Condition) ? null : Condition!.Trim();

    /// <summary>The lines the assistant's tracepoints wrote in <paramref name="output"/>, in order and without their mark;
    /// nothing else of the output.</summary>
    public static IReadOnlyList<string> TraceLines(string? output)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(output)) return lines;
        foreach (var raw in output!.Replace("\r\n", "\n").Split('\n'))
        {
            var at = raw.IndexOf(TraceMarker, StringComparison.Ordinal);
            if (at >= 0) lines.Add(raw.Substring(at + TraceMarker.Length).TrimEnd());
        }
        return lines;
    }
}

/// <summary>
/// What the tracepoints wrote since a place in the editor's debug output: <see cref="Lines"/> (marked ones only),
/// <see cref="Next"/> the place to read from next time, <see cref="Cut"/> when output written since that place is no longer
/// held (the reader keeps only the end of a long output).
/// </summary>
/// <remarks>⚠ A place, never a count of lines already shown: the output is read through a window of its last characters,
/// which drops its oldest text and is emptied by a new session — a count then hides new lines as seen, or shows old ones
/// as new. ⚠ And opaque, each editor's own: Visual Studio empties its Debug pane when a session starts, and a pane rewritten
/// to about the same length is told from the one marked only by the text the place stood after — a bare length would
/// read from there and lose the run's first lines.</remarks>
internal sealed record TraceRead(IReadOnlyList<string> Lines, string? Next, bool Cut);

/// <summary>
/// What happened when a session was asked to start — three outcomes, not two.
/// </summary>
/// <remarks>
/// Collapsing "could not start" into "ran without stopping" is a lie the model cannot detect, and
/// it is the most likely outcome of the two front-ends: under Visual Studio a failed build never
/// leaves design mode, and under VS Code a workspace with no launch configuration cannot start at
/// all. Both are ordinary situations with an actionable answer — <b>and neither is "your breakpoint
/// was never reached"</b>, which is what the agent would otherwise be told to investigate.
/// </remarks>
/// <param name="State">Where execution paused, when it did.</param>
/// <param name="Failure">
/// Why the session could not run, in the adapter's own words. Non-null only when nothing ran.
/// </param>
/// <param name="StillRunning">
/// The program started and is still running under the debugger, with no stop within the start
/// budget. ⚠ Neither "did not start" (the agent believes no session exists, and its next start is
/// refused as "already running") nor "ran to completion" (the session's breakpoints would be
/// removed from a live run).
/// </param>
internal sealed record DebugStartResult(DebugStopState? State, string? Failure, bool StillRunning = false)
{
    /// <summary>The program started and reached a stop.</summary>
    internal static DebugStartResult Stopped(DebugStopState state) => new(state, null);

    /// <summary>The program started, ran to the end, and never stopped.</summary>
    internal static DebugStartResult RanToCompletion { get; } = new(null, null);

    /// <summary>Nothing ran.</summary>
    internal static DebugStartResult Failed(string reason) => new(null, reason);

    /// <summary>The program started and is still running: no stop within the start budget.</summary>
    internal static DebugStartResult NoStopYet { get; } = new(null, null, StillRunning: true);
}

/// <summary>What a resume (continue or step) came to.</summary>
internal enum DebugResumeOutcome
{
    /// <summary>Execution paused again: <see cref="DebugResumeResult.State"/> says where.</summary>
    Stopped,
    /// <summary>The program ran to its end: the session is over.</summary>
    Ended,
    /// <summary>The program is still running past the resume budget: the session is live.</summary>
    StillRunning,
    /// <summary>No session was paused, so there was nothing to resume.</summary>
    NotPaused,
    /// <summary>The resume itself failed or got no answer: <see cref="DebugResumeResult.Failure"/> says why.</summary>
    Failed,
}

/// <summary>
/// What a resume came to — five outcomes, because each one asks something different of the caller.
/// </summary>
/// <remarks>
/// ⚠ Read as one "no stop", an ENDED run keeps the assistant's breakpoints: they go when the session ends, and only a
/// start that ran to completion says so — while the commonest end of an agent's session is a <c>continue</c> after the
/// last inspection. They then stay in the editor's saved list and stop the user's next run on lines they never chose.
/// A run STILL RUNNING keeps them; a resume that FAILED is neither.
/// </remarks>
internal sealed record DebugResumeResult(DebugResumeOutcome Outcome, DebugStopState? State = null, string? Failure = null)
{
    internal static DebugResumeResult Stopped(DebugStopState state) => new(DebugResumeOutcome.Stopped, state);
    internal static DebugResumeResult Ended { get; } = new(DebugResumeOutcome.Ended);
    internal static DebugResumeResult StillRunning { get; } = new(DebugResumeOutcome.StillRunning);
    internal static DebugResumeResult NotPaused { get; } = new(DebugResumeOutcome.NotPaused);
    internal static DebugResumeResult Failed(string reason) => new(DebugResumeOutcome.Failed, Failure: reason);
}

/// <summary>
/// The editor's debugger did not answer — the channel to it failed, or it never came back within its budget.
/// </summary>
/// <remarks>
/// ⚠ Never an ordinary answer. Returned as one (<c>null</c>, <c>false</c>, an empty list), a debugger held by a dialog,
/// or an editor whose request failed, reads as "the debugger refused a breakpoint (no executable code on that line?)",
/// "no paused debug session", "the program ended" — and the model moves its breakpoint, or concludes the code never
/// ran, about a debugger that was never reached. The message says so in words the model reads as they are.
/// </remarks>
internal sealed class DebuggerNotAnsweringException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The debugger's state while execution is paused.
/// </summary>
/// <param name="ThreadId">
/// Taken from the adapter's stop notification and echoed back on the next request. Never assumed:
/// a Node adapter reports thread <c>0</c>, not <c>1</c>.
/// </param>
/// <param name="LocalsFrameId">
/// <see cref="DebugFrame.Id"/> of the frame <paramref name="Locals"/> were read from, or
/// <c>null</c> when the producer cannot say.
/// <para>
/// ⚠ <b>It is not always the top frame, and it is not always a frame the model can see.</b> The
/// four producers disagree by construction: Visual Studio reads the IDE's <i>selected</i> frame
/// (which Just My Code moves off the top, and which <c>debug_inspect</c> moves itself when it
/// scopes an evaluation), VS Code's ordinary capture reads the top frame, and its <c>/tdd</c>
/// capture reads the first frame under the workspace root. On top of that the renderer hides
/// frames outside the workspace, so the frame printed first is often not the one the locals came
/// from. Labelling them "current frame" therefore attributed a runtime frame's variables to the
/// user's code — a wrong answer the model cannot detect. Producers fill this in;
/// <c>DebugStateFormatter</c> names the frame instead of claiming one, and claims nothing when
/// this is <c>null</c>.
/// </para>
/// </param>
internal sealed record DebugStopState(
    string Reason,
    int ThreadId,
    IReadOnlyList<DebugFrame> Frames,
    IReadOnlyList<DebugVariable> Locals,
    string? Exception = null,
    int? LocalsFrameId = null);

/// <summary>
/// Port abstracting the host editor's debugger, so the agent tools never reference an editor SDK.
/// Implemented per front-end: Visual Studio drives EnvDTE from the in-process package, VS Code
/// drives the Debug Adapter Protocol from the TypeScript extension over the reverse RPC.
/// </summary>
/// <remarks>
/// <para>
/// The seven operations below were driven end to end, without a human click, in <b>both</b>
/// front-ends before this interface existed; those probes are kept in
/// <c>docs/probes/debug-feasibility/</c>.
/// </para>
/// <para>
/// <b>Consent model.</b> Starting a session <i>executes the user's program</i>, so
/// <see cref="StartAsync"/> is gated by <c>IApprovalService</c> in the tool layer — same family as
/// <c>run_command</c>. Everything after that (breakpoints, stepping, reading, evaluating) is
/// <b>not</b> gated: the execution it observes is already consented to, and a prompt per step would
/// make the loop unusable. The granularity of consent is the <b>session</b>, not the step.
/// </para>
/// <para>
/// Every operation is best-effort and must not throw for an ordinary debugger condition (no
/// session, program exited, expression invalid): they return <c>null</c> or an empty list, which
/// callers render as a plain sentence. ⚠ A debugger that did not ANSWER is not one of them: the
/// channel failing, or no answer within the budget, throws <see cref="DebuggerNotAnsweringException"/>
/// — and <see cref="StartAsync"/> / the resumes say it in their result instead.
/// <c>OperationCanceledException</c> propagates, as everywhere else in this codebase.
/// </para>
/// </remarks>
internal interface IDebugSession
{
    /// <summary>
    /// False when this front-end cannot drive a debugger at all (no in-process package loaded, no
    /// RPC peer). Distinct from "no session running": the tools use it to say why rather than to
    /// claim nothing is being debugged.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Adds a breakpoint, returning it as the debugger bound it — with what it took of
    /// <paramref name="options"/> — or <c>null</c> on refusal.</summary>
    Task<DebugBreakpointInfo?> AddBreakpointAsync(string file, int line, BreakpointOptions? options, CancellationToken ct);

    /// <summary>What the assistant's tracepoints wrote in this editor's debug output after the place
    /// <paramref name="since"/> (an earlier <see cref="TraceRead.Next"/>), oldest first, without their mark. A place the
    /// output no longer holds (emptied by a new session) reads it from its start; <c>null</c> returns only where it ends
    /// now (no lines).</summary>
    Task<TraceRead> ReadTraceAsync(string? since, CancellationToken ct);

    /// <summary>Removes a breakpoint. <c>false</c> when there was none at that location.</summary>
    Task<bool> RemoveBreakpointAsync(string file, int line, CancellationToken ct);

    /// <summary>Every breakpoint currently set, whoever set it.</summary>
    Task<IReadOnlyList<DebugBreakpointInfo>> ListBreakpointsAsync(CancellationToken ct);

    /// <summary>
    /// Starts debugging and waits for the first stop. The three outcomes are distinguished by
    /// <see cref="DebugStartResult"/>; this is the one operation where they differ enough to matter.
    /// </summary>
    Task<DebugStartResult> StartAsync(CancellationToken ct);

    /// <summary>
    /// Resumes and waits for the next stop. The five outcomes are kept apart by <see cref="DebugResumeResult"/>: a run
    /// that ended takes the assistant's breakpoints with it, one still running keeps them.
    /// </summary>
    Task<DebugResumeResult> ContinueAsync(CancellationToken ct);

    /// <summary>Advances one step and waits for the stop that follows; same outcomes as <see cref="ContinueAsync"/>.</summary>
    Task<DebugResumeResult> StepAsync(DebugStepKind kind, CancellationToken ct);

    /// <summary>The current paused state, or <c>null</c> when execution is not paused.</summary>
    Task<DebugStopState?> GetStateAsync(CancellationToken ct);

    /// <summary>
    /// Evaluates an expression in the scope of <paramref name="frameId"/> (the top frame when
    /// <c>null</c>). Returns the adapter's rendering, or <c>null</c> when the expression could not
    /// be evaluated — an invalid expression is an ordinary answer, not an error.
    /// </summary>
    Task<string?> EvaluateAsync(string expression, int? frameId, CancellationToken ct);

    /// <summary>Terminates the session. Safe to call when none is running.</summary>
    Task StopAsync(CancellationToken ct);
}
