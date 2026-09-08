using System.Text.Json;
using Inferpal.Services.Debugging;

namespace Inferpal.Services.Tools;

/// <summary>
/// Exposes the editor's debugger break state to the agent. Read-only: also backs the
/// <c>@debugger</c> mention.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two channels, and the second one was missing.</b> Visual Studio <i>pushes</i> its break state
/// cross-process (<see cref="Inferpal.GhostText.VsDebuggerTracker"/> → <see cref="DebuggerStateSignal"/>),
/// so a snapshot is already on disk when the model asks. Every other front-end answers on demand,
/// through <see cref="IDebugSession"/> — VS Code drives the Debug Adapter Protocol from its
/// extension. This tool read the pushed snapshot and nothing else, so under VS Code it replied
/// <i>"No paused debug session"</i> to a user stopped at a breakpoint: not a missing capability but
/// a wrong answer, and one the model cannot detect (§21's own rule about collapsing "could not
/// start" into "ran without stopping", wearing a different hat).
/// </para>
/// <para>
/// The signal is still consulted first where it exists: it costs no round trip and survives a
/// command driver that never advertised itself, which is the case a devenv whose in-process package
/// failed to load degrades to.
/// </para>
/// </remarks>
internal sealed class GetDebuggerStateTool(IDebugSession? session = null, Func<string>? root = null) : ITool
{
    public const string ToolName = "get_debugger_state";

    public string Name => ToolName;

    public string Description =>
        "Returns the editor's current debugger state when execution is paused at a breakpoint "
        + "or exception: break reason, exception type/message, call stack (top frames with file:line), "
        + "and the local variables of the current frame. Returns a clear message when no debug session "
        + "is paused. Use this to diagnose runtime failures the user is currently debugging.";

    public object Parameters => new
    {
        type       = "object",
        properties = new { },
        required   = Array.Empty<string>(),
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        // Pushed snapshot (Visual Studio): already on disk, no round trip.
        if (DebuggerStateSignal.TryRead() is { } snap) return DebuggerStateSignal.Format(snap);

        // On-demand port (VS Code today): the editor is asked only when nothing was pushed.
        if (session is { IsAvailable: true } live && await live.GetStateAsync(ct) is { } state)
            return DebugStateFormatter.Format(state, root?.Invoke());

        return "No paused debug session. Start debugging and hit a breakpoint (or an exception), "
             + "then call this tool again.";
    }
}
