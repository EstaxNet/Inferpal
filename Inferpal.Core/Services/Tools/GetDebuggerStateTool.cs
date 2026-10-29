using System.Text.Json;
using Inferpal.Services.Debugging;

namespace Inferpal.Services.Tools;

/// <summary>
/// Exposes the editor's debugger break state to the agent. Read-only: also backs the
/// <c>@debugger</c> mention.
/// </summary>
/// <remarks>
/// <b>The answer lives in <see cref="DebuggerStateReader"/>, not here.</b> Shared with the two <c>@debugger</c>
/// mentions, it keeps the three call sites on one answer: read separately, one says "no paused session" to a user
/// stopped at a breakpoint — a wrong answer the model cannot detect.
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
        => await DebuggerStateReader.TryReadAsync(session, root?.Invoke(), ct)
           ?? "No paused debug session. Start debugging and hit a breakpoint (or an exception), "
            + "then call this tool again.";
}
