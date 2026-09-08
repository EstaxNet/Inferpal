using Inferpal.Services.Signals;

namespace Inferpal.Services.Debugging;

/// <summary>
/// The single answer to "is the user's debugger paused, and where?" — read by the
/// <c>get_debugger_state</c> tool and by the <c>@debugger</c> mention, in both front-ends.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two channels, and they are not interchangeable.</b> Visual Studio <i>pushes</i> its break
/// state cross-process (<c>VsDebuggerTracker</c> → <see cref="DebuggerStateSignal"/>), so a
/// snapshot is already on disk when the question is asked; every other front-end answers on demand
/// through <see cref="IDebugSession"/> (VS Code drives the Debug Adapter Protocol). The pushed
/// snapshot is consulted first where it exists: it costs no round trip and survives a command
/// driver that never advertised itself, which is what a devenv whose in-process package failed to
/// load degrades to.
/// </para>
/// <para>
/// ⚠ <b>One reader, never two.</b> Three call sites ask this question — the tool, the VS mention and
/// the VS Code mention — and each of them used to answer it its own way. That is how the VS Code
/// mention ended up attaching a session <i>name</i> where Visual Studio attached a call stack, and
/// how the tool ended up answering "not paused" to a VS Code user stopped at a breakpoint. Callers
/// distinguish the two outcomes by <c>null</c>, never by matching the sentence a tool returned —
/// matching a tool's own message is the §18 mistake wearing a different hat.
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
        IDebugSession? session, string? rootDir, CancellationToken ct)
    {
        if (DebuggerStateSignal.TryRead() is { } pushed) return DebuggerStateSignal.Format(pushed);

        if (session is { IsAvailable: true } live && await live.GetStateAsync(ct) is { } state)
            return DebugStateFormatter.Format(state, rootDir);

        return null;
    }
}
