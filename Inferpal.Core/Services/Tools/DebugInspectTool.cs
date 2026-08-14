using System.Text.Json;
using Inferpal.Services.Debugging;

namespace Inferpal.Services.Tools;

/// <summary>
/// Reads a paused debugger: call stack, locals, and arbitrary expression evaluation.
/// Everything that <i>observes</i>; <see cref="DebugControlTool"/> is everything that acts.
/// </summary>
/// <remarks>
/// No approval, by the consent rule of §21: the execution being observed was already approved when
/// the session started, and reading it changes nothing. No budget either — reading does not advance
/// the program, so it cannot loop forever the way stepping can.
/// </remarks>
internal sealed class DebugInspectTool(IDebugSession session, Func<string> root) : ITool
{
    public const string ToolName = "debug_inspect";

    public string Name => ToolName;

    public string Description =>
        "Reads the state of a paused debugger: stop reason, call stack, and the local variables of "
        + "the current frame; or evaluates an expression in the scope of the current frame "
        + "(action='evaluate'). Values are rendered by the debugger itself — read them, do not "
        + "assume a format. Requires a session paused by debug_control.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            action     = new { type = "string", description = "'state' (default) or 'evaluate'." },
            expression = new { type = "string", description = "Expression to evaluate in the current frame. Required for action='evaluate'." },
        },
        required = Array.Empty<string>(),
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (!session.IsAvailable)
            return "No debugger is reachable from this editor session. Do not retry; reason from the "
                 + "source instead, or ask the user to run the program.";

        var action = args.TryGetProperty("action", out var a) ? a.GetString()?.Trim().ToLowerInvariant() : null;

        if (action == "evaluate")
        {
            if (!args.TryGetProperty("expression", out var e) || e.GetString() is not { Length: > 0 } expression)
                return "Error: 'expression' is required for action='evaluate'.";

            var state = await session.GetStateAsync(ct);
            if (state is null) return NotPaused;

            var frameId = state.Frames.Count > 0 ? state.Frames[0].Id : (int?)null;
            var value   = await session.EvaluateAsync(expression, frameId, ct);
            return value is null
                ? $"The debugger could not evaluate `{expression}` in this frame (unknown symbol, or not "
                + "in scope here). Check the locals before retrying with another expression."
                : $"`{expression}` = {DebugStateFormatter.Cap(value)}";
        }

        if (!string.IsNullOrEmpty(action) && action != "state")
            return $"Unknown action '{action}'. Valid: 'state' (default) or 'evaluate'.";

        var paused = await session.GetStateAsync(ct);
        return paused is null ? NotPaused : DebugStateFormatter.Format(paused, root());
    }

    private const string NotPaused =
        "Execution is not paused. Set a breakpoint and start a session with debug_control first, or "
        + "the program is still running — use debug_control action='continue' only if it is paused.";
}
