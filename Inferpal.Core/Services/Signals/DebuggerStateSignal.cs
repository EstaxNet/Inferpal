using System.IO;
using Inferpal.Services.Debugging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inferpal.Services.Signals;

/// <summary>One captured stack frame (top-of-stack first).</summary>
internal sealed record DebuggerFrame(
    [property: JsonPropertyName("function")] string Function,
    [property: JsonPropertyName("file")]     string? File,
    [property: JsonPropertyName("line")]     int? Line);

/// <summary>One local variable of the current stack frame.</summary>
internal sealed record DebuggerLocal(
    [property: JsonPropertyName("name")]  string Name,
    [property: JsonPropertyName("type")]  string Type,
    [property: JsonPropertyName("value")] string Value);

/// <summary>Snapshot of the debugger at the moment it entered break mode.</summary>
internal sealed record DebuggerSnapshot(
    [property: JsonPropertyName("reason")]    string Reason,
    [property: JsonPropertyName("exception")] string? Exception,
    [property: JsonPropertyName("frames")]    IReadOnlyList<DebuggerFrame> Frames,
    [property: JsonPropertyName("locals")]    IReadOnlyList<DebuggerLocal> Locals,
    [property: JsonPropertyName("pid")]       int Pid,
    [property: JsonPropertyName("ts")]        long Ts);

/// <summary>
/// File-based IPC channel publishing the debugger break state from the in-process
/// <see cref="Inferpal.GhostText.GhostTextPackage"/> (which can use the EnvDTE debugger
/// automation) to the out-of-process extension host (<c>@debugger</c> mention and the
/// <c>get_debugger_state</c> tool), which has no debugger API at all.
/// </summary>
/// <remarks>
/// Protocol mirrors <see cref="ActiveSolutionSignal"/>: the in-process tracker writes on
/// every break, clears when the debugger runs or stops. The state is persistent while VS
/// stays paused (a break can last hours), so readers cannot expire it by age — instead the
/// snapshot records the devenv process id, and a snapshot whose process is gone is treated
/// as stale (left behind by a crashed session).
/// </remarks>
internal static class DebuggerStateSignal
{
    /// <summary>Full path of the signal file.</summary>
    internal static string FilePath => SignalFile.PathFor("debugger_state.json");

    // ── In-process side (VsDebuggerTracker) ────────────────────────────────────

    internal static void Write(DebuggerSnapshot snapshot)
    {
        SignalFile.Write(FilePath, snapshot, "DebuggerStateSignal.Write");
    }

    /// <summary>Deletes the signal (debugger resumed or stopped). Safe if absent.</summary>
    internal static void Clear()
    {
        SignalFile.Delete(FilePath);
    }

    // ── Out-of-process side (mention / tool) ───────────────────────────────────

    /// <summary>
    /// The current break snapshot, or <c>null</c> when the debugger is not paused (no file,
    /// unreadable file, or a stale file whose VS process no longer exists).
    /// </summary>
    internal static DebuggerSnapshot? TryRead()
    {
        // §22: same reason as ActiveSolutionSignal — a host with no in-process VS peer would report
        // Visual Studio's break state as its own. `get_debugger_state` then reads "No paused debug
        // session", which is true of this editor, instead of handing the model another editor's
        // call stack.
        if (!SignalScope.HasVsInProcessPeer) return null;

        var snap = SignalFile.TryRead<DebuggerSnapshot>(FilePath);
        return snap is not null && SignalFile.IsProcessAlive(snap.Pid) ? snap : null;
    }

    /// <summary>
    /// Renders a snapshot as the markdown context block shared by the <c>@debugger</c>
    /// attachment and the <c>get_debugger_state</c> tool result (model-facing, English).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Delegates to <see cref="DebugStateFormatter"/>: this channel and the §21 port describe the
    /// same thing to the same reader, and they had two renderers producing two vocabularies —
    /// "Debugger state (paused)" / "Break reason" here against "Debugger paused" / "Stop reason"
    /// there, for one concept.
    /// </para>
    /// <para>
    /// <b>The drift was not only cosmetic.</b> The §21 renderer caps the stack, the locals and each
    /// value, because a nine-frame stack of runtime internals spends a 16k window on noise. This
    /// path had no cap at all, so <c>@debugger</c> on a deep stack pasted the whole thing. Sharing
    /// the renderer gives it the budget the other path already had.
    /// </para>
    /// </remarks>
    internal static string Format(DebuggerSnapshot snap) =>
        DebugStateFormatter.Format(ToStopState(snap));

    /// <summary>
    /// Maps the tracker's snapshot onto the port's shape. Frame ids are positional: EnvDTE
    /// numbers stack frames from the top, and nothing on this path selects a frame by id.
    /// </summary>
    private static DebugStopState ToStopState(DebuggerSnapshot snap) =>
        new(snap.Reason,
            ThreadId: 0,
            Frames: [.. snap.Frames.Select((f, i) => new DebugFrame(i, f.Function, f.File, f.Line))],
            Locals: [.. snap.Locals.Select(l => new DebugVariable(l.Name, l.Type, l.Value))],
            Exception: snap.Exception);
}
