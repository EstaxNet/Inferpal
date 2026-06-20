using System.Text.Json;

namespace Inferpal.Services.Tools;

/// <summary>
/// Exposes the Visual Studio debugger break state (published cross-process by
/// <see cref="Inferpal.GhostText.VsDebuggerTracker"/> via <see cref="DebuggerStateSignal"/>)
/// to the agent. Read-only: also backs the <c>@debugger</c> mention.
/// </summary>
internal sealed class GetDebuggerStateTool : ITool
{
    public const string ToolName = "get_debugger_state";

    public string Name => ToolName;

    public string Description =>
        "Returns the current Visual Studio debugger state when execution is paused at a breakpoint " +
        "or exception: break reason, exception type/message, call stack (top frames with file:line), " +
        "and the local variables of the current frame. Returns a clear message when no debug session " +
        "is paused. Use this to diagnose runtime failures the user is currently debugging.";

    public object Parameters => new
    {
        type       = "object",
        properties = new { },
        required   = Array.Empty<string>(),
    };

    public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var snap = DebuggerStateSignal.TryRead();
        return Task.FromResult(snap is null
            ? "No paused debug session. Start debugging and hit a breakpoint (or an exception), then call this tool again."
            : DebuggerStateSignal.Format(snap));
    }
}
