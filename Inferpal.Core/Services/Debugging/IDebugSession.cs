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
/// the debug adapter, not by us, and the two front-ends disagree on purpose. The feasibility probe
/// (roadmap §21) measured the same string list shown as <c>"probe-42"</c> / <c>Count = 3</c> under
/// Visual Studio and <c>'probe-42'</c> / <c>(3) [21, 42, 43]</c> under VS Code. Present them,
/// never parse them, never compare them across editors — that is the §18 mistake (matching a
/// tool's own message) wearing a different hat.
/// </remarks>
internal sealed record DebugVariable(string Name, string Type, string Value);

/// <summary>A breakpoint as the debugger knows it.</summary>
internal sealed record DebugBreakpointInfo(string File, int Line, bool Enabled);

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
internal sealed record DebugStartResult(DebugStopState? State, string? Failure)
{
    /// <summary>The program started and reached a stop.</summary>
    internal static DebugStartResult Stopped(DebugStopState state) => new(state, null);

    /// <summary>The program started, ran to the end, and never stopped.</summary>
    internal static DebugStartResult RanToCompletion { get; } = new(null, null);

    /// <summary>Nothing ran.</summary>
    internal static DebugStartResult Failed(string reason) => new(null, reason);
}

/// <summary>
/// The debugger's state while execution is paused.
/// </summary>
/// <param name="ThreadId">
/// Taken from the adapter's stop notification and echoed back on the next request. Never assumed:
/// the probe's Node adapter reported thread <c>0</c>, not <c>1</c>.
/// </param>
internal sealed record DebugStopState(
    string Reason,
    int ThreadId,
    IReadOnlyList<DebugFrame> Frames,
    IReadOnlyList<DebugVariable> Locals,
    string? Exception = null);

/// <summary>
/// Port abstracting the host editor's debugger, so the agent tools never reference an editor SDK.
/// Implemented per front-end: Visual Studio drives EnvDTE from the in-process package, VS Code
/// drives the Debug Adapter Protocol from the TypeScript extension over the reverse RPC.
/// </summary>
/// <remarks>
/// <para>
/// Feasibility measured on 2026-08-04 (roadmap §21) before this interface existed: the seven
/// operations below were driven end to end, without a human click, in <b>both</b> front-ends.
/// The probes are kept in <c>docs/probes/debug-feasibility/</c>.
/// </para>
/// <para>
/// <b>Consent model, locked before the code was written.</b> Starting a session <i>executes the
/// user's program</i>, so <see cref="StartAsync"/> is gated by <c>IApprovalService</c> in the tool
/// layer — same family as <c>run_command</c>. Everything after that (breakpoints, stepping,
/// reading, evaluating) is <b>not</b> gated: the execution it observes is already consented to, and
/// a prompt per step would make the loop unusable. The granularity of consent is the
/// <b>session</b>, not the step.
/// </para>
/// <para>
/// Every operation is best-effort and must not throw for an ordinary debugger condition (no
/// session, program exited, expression invalid): they return <c>null</c> or an empty list, which
/// callers render as a plain sentence. <c>OperationCanceledException</c> is the only exception that
/// propagates, as everywhere else in this codebase.
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

    /// <summary>Adds a breakpoint, returning it as the debugger bound it, or <c>null</c> on refusal.</summary>
    Task<DebugBreakpointInfo?> AddBreakpointAsync(string file, int line, CancellationToken ct);

    /// <summary>Removes a breakpoint. <c>false</c> when there was none at that location.</summary>
    Task<bool> RemoveBreakpointAsync(string file, int line, CancellationToken ct);

    /// <summary>Every breakpoint currently set, whoever set it.</summary>
    Task<IReadOnlyList<DebugBreakpointInfo>> ListBreakpointsAsync(CancellationToken ct);

    /// <summary>
    /// Starts debugging and waits for the first stop. The three outcomes are distinguished by
    /// <see cref="DebugStartResult"/>; this is the one operation where they differ enough to matter.
    /// </summary>
    Task<DebugStartResult> StartAsync(CancellationToken ct);

    /// <summary>Resumes and waits for the next stop. <c>null</c> when the program ended.</summary>
    Task<DebugStopState?> ContinueAsync(CancellationToken ct);

    /// <summary>Advances one step and waits for the stop that follows.</summary>
    Task<DebugStopState?> StepAsync(DebugStepKind kind, CancellationToken ct);

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
