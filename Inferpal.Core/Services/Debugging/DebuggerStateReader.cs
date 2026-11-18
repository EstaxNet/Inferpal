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

    /// <summary>How many trace lines are shown at most; the earlier ones are counted.</summary>
    internal const int TraceShown = 50;

    /// <summary>
    /// What the assistant's tracepoints wrote in this session (since <see cref="AgentBreakpoints.TraceStart"/>), as a
    /// section to put after the state, or <c>null</c> when none was set, they wrote nothing, or the debugger cannot say.
    /// </summary>
    /// <remarks>Read whether or not the program is paused — a tracepoint's whole point is a run that does not stop — and
    /// only while a tracepoint of the assistant is set: otherwise every state read would cost a round trip, and a run
    /// long over, the user's own sessions included, would show its trace as the current one.</remarks>
    internal static async Task<string?> TryReadTraceAsync(IDebugSession? session, CancellationToken ct)
    {
        if (session is not AgentCleaningDebugSession { Agent: { HasTracepoints: true, TraceStart: { } start } }) return null;
        TraceRead read;
        try { read = await session.ReadTraceAsync(start, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("DebuggerStateReader.Trace", ex);
            return "The tracepoints' output could not be read: " + ex.Message;
        }
        return read.Lines.Count == 0 && !read.Cut ? null : TraceSection(read);
    }

    /// <summary>The trace as the model reads it: how many lines, the last <see cref="TraceShown"/> of them, and whether
    /// earlier output was no longer held. One wording for the state and for a run's result.</summary>
    internal static string TraceSection(TraceRead read)
    {
        var shown = read.Lines.Skip(Math.Max(0, read.Lines.Count - TraceShown)).ToList();
        var notes = new List<string>();
        if (read.Lines.Count > shown.Count) notes.Add($"the first {read.Lines.Count - shown.Count} not shown");
        if (read.Cut) notes.Add("earlier output was no longer held: lines before these were lost");
        return $"Tracepoints wrote {read.Lines.Count} line(s)" + (notes.Count > 0 ? $" ({string.Join("; ", notes)})" : string.Empty)
               + (shown.Count > 0 ? ":\n" + string.Join("\n", shown) : ".");
    }

    /// <summary>Whether <paramref name="session"/> is Visual Studio's, whose driver may be the one thing not running.</summary>
    private static bool IsVisualStudios(IDebugSession? session) =>
        session is SignalDebugSession or AgentCleaningDebugSession { Inner: SignalDebugSession };
}
