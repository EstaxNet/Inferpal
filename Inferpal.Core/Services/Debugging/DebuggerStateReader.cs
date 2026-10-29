using Inferpal.Services.Signals;

namespace Inferpal.Services.Debugging;

/// <summary>
/// The single answer to "is the user's debugger paused, and where?" — read by the
/// <c>get_debugger_state</c> tool and by the <c>@debugger</c> mention, in both front-ends.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asked, never pushed</b>: every front-end answers through <see cref="IDebugSession"/> — Visual Studio's in-process
/// driver captures the state when the question comes (<see cref="SignalDebugSession"/>), VS Code drives the Debug
/// Adapter Protocol. ⚠ Captured at every stop instead, the stack, the locals and the exception are read on Visual
/// Studio's UI thread at each step of the user's debugging session, whether anyone asks or not.
/// </para>
/// <para>
/// ⚠ <b>One reader, never two.</b> Three call sites ask this question — the tool, the VS mention and
/// the VS Code mention. Answered separately, one attaches a session <i>name</i> where the other
/// attaches a call stack, and the tool says "not paused" to a user stopped at a breakpoint. Callers
/// distinguish the two outcomes by <c>null</c>, never by matching the sentence a tool returned:
/// reading a tool's own message back is never how a caller decides.
/// </para>
/// </remarks>
internal static class DebuggerStateReader
{
    /// <summary>
    /// The paused debugger rendered for the model, or <c>null</c> when nothing is paused —
    /// no session, none reachable from this editor, or one that is running.
    /// </summary>
    /// <param name="session">The editor's debugger port; <c>null</c> when this front-end has none.</param>
    /// <param name="rootDir">Workspace root, used to keep the stack to the user's own frames.</param>
    internal static async Task<string?> TryReadAsync(
        IDebugSession? session, string? rootDir, CancellationToken ct) =>
        await TryReadStateAsync(session, ct) is { } state ? DebugStateFormatter.Format(state, rootDir) : null;

    /// <summary>The paused debugger as the port shapes it, or <c>null</c> when nothing is paused: what a caller that also
    /// needs the frames' files reads.</summary>
    /// <exception cref="DebuggerNotAnsweringException">The debugger did not answer — Visual Studio's in-process driver
    /// not running included: under Visual Studio there is always a debugger, and one that cannot be reached is never one
    /// that is not paused.</exception>
    internal static async Task<DebugStopState?> TryReadStateAsync(IDebugSession? session, CancellationToken ct) =>
        session is { IsAvailable: true } || IsVisualStudios(session) ? await session!.GetStateAsync(ct) : null;

    /// <summary>Whether <paramref name="session"/> is Visual Studio's, whose driver may be the one thing not running.</summary>
    private static bool IsVisualStudios(IDebugSession? session) =>
        session is SignalDebugSession or AgentCleaningDebugSession { Inner: SignalDebugSession };
}
