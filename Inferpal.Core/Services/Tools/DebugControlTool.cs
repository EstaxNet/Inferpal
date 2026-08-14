using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Debugging;

namespace Inferpal.Services.Tools;

/// <summary>
/// Drives the host editor's debugger: breakpoints, starting a session, resuming and stepping.
/// Everything that <i>acts</i>; <see cref="DebugInspectTool"/> is everything that reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Consent, decided before this file existed (roadmap §21).</b> <c>start</c> executes the user's
/// program, so it goes through <see cref="IApprovalService"/> exactly like <c>run_command</c>. No
/// other action prompts: once the session is consented to, the breakpoints and steps that observe
/// it are not separate executions, and a prompt per step would make the loop unusable. The
/// granularity of consent is the session, not the step.
/// </para>
/// <para>
/// Registered only when the front-end supplies an <see cref="IDebugSession"/>. A front-end without
/// one does not show the model a tool it cannot honour.
/// </para>
/// </remarks>
internal sealed class DebugControlTool(
    IDebugSession session,
    IApprovalService approval,
    DebugStepBudget budget,
    Func<string> root) : ITool
{
    public const string ToolName = "debug_control";

    public string Name => ToolName;

    public string Description =>
        "Drives the debugger: set or clear breakpoints, start a debugging session, resume, and step. "
        + "Starting a session runs the user's program and asks them for confirmation first. "
        + "Use this to test a hypothesis about runtime behaviour instead of guessing from the source: "
        + "set a breakpoint where the state matters, start, then read the state with debug_inspect. "
        + "Actions: set_breakpoint, clear_breakpoint, list_breakpoints, start, continue, step_over, "
        + "step_into, step_out, stop.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            action = new
            {
                type = "string",
                description = "set_breakpoint | clear_breakpoint | list_breakpoints | start | continue | "
                            + "step_over | step_into | step_out | stop",
            },
            file = new { type = "string", description = "File holding the breakpoint. Required for set_breakpoint/clear_breakpoint." },
            line = new { type = "integer", description = "1-based line number. Required for set_breakpoint/clear_breakpoint." },
        },
        required = new[] { "action" },
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (!session.IsAvailable)
            return "No debugger is reachable from this editor session. Do not retry; reason from the "
                 + "source instead, or ask the user to run the program.";

        var action = args.TryGetProperty("action", out var a) ? a.GetString()?.Trim().ToLowerInvariant() : null;
        if (string.IsNullOrEmpty(action))
            return "Error: 'action' is required (set_breakpoint, clear_breakpoint, list_breakpoints, "
                 + "start, continue, step_over, step_into, step_out, stop).";

        return action switch
        {
            "set_breakpoint"   => await SetBreakpointAsync(args, ct),
            "clear_breakpoint" => await ClearBreakpointAsync(args, ct),
            "list_breakpoints" => await ListBreakpointsAsync(ct),
            "start"            => await StartAsync(ct),
            "continue"         => await ResumeAsync(null, ct),
            "step_over"        => await ResumeAsync(DebugStepKind.Over, ct),
            "step_into"        => await ResumeAsync(DebugStepKind.Into, ct),
            "step_out"         => await ResumeAsync(DebugStepKind.Out, ct),
            "stop"             => await StopAsync(ct),
            _                  => $"Unknown action '{action}'. Valid: set_breakpoint, clear_breakpoint, "
                                + "list_breakpoints, start, continue, step_over, step_into, step_out, stop.",
        };
    }

    private (string? File, int Line, string? Error) ReadLocation(JsonElement args)
    {
        if (!args.TryGetProperty("file", out var f) || f.GetString() is not { Length: > 0 } raw)
            return (null, 0, "Error: 'file' is required for this action.");

        if (!args.TryGetProperty("line", out var l) || !l.TryGetInt32(out var line) || line < 1)
            return (null, 0, "Error: 'line' is required for this action and must be a 1-based line number.");

        var full = PathSanitizer.Sanitize(raw);
        try { PathSanitizer.AssertUnderRoot(full, root()); }
        catch (Exception ex) { return (null, 0, $"Error: {ex.Message}"); }

        return (full, line, null);
    }

    private async Task<string> SetBreakpointAsync(JsonElement args, CancellationToken ct)
    {
        var (file, line, error) = ReadLocation(args);
        if (error is not null) return error;

        var bp = await session.AddBreakpointAsync(file!, line, ct);
        return bp is null
            ? $"The debugger refused a breakpoint at {file}:{line} (no executable code on that line?). "
            + "Try the first executable line of the statement."
            : $"Breakpoint set at {bp.File}:{bp.Line}.";
    }

    private async Task<string> ClearBreakpointAsync(JsonElement args, CancellationToken ct)
    {
        var (file, line, error) = ReadLocation(args);
        if (error is not null) return error;

        return await session.RemoveBreakpointAsync(file!, line, ct)
            ? $"Breakpoint cleared at {file}:{line}."
            : $"No breakpoint at {file}:{line}.";
    }

    private async Task<string> ListBreakpointsAsync(CancellationToken ct)
    {
        var all = await session.ListBreakpointsAsync(ct);
        if (all.Count == 0) return "No breakpoints are set.";

        return "Breakpoints:\n" + string.Join('\n', all.Select(b =>
            $"- {b.File}:{b.Line}{(b.Enabled ? string.Empty : " (disabled)")}"));
    }

    private async Task<string> StartAsync(CancellationToken ct)
    {
        // The one action that executes. Subject is the workspace root: a raw, matchable value for
        // the permission rules, in the spirit of run_command passing the raw command.
        var workspace = root();
        if (!await approval.RequestApprovalAsync(
                ToolName, Strings.DebugStartConfirm(workspace), ct, subject: workspace))
            return Strings.DebugStartCancelled;

        budget.Reset();
        var result = await session.StartAsync(ct);

        // Three outcomes, deliberately not two: "it could not start" must never be dressed up as
        // "your breakpoint was never reached", which would send the agent hunting a bug in code that
        // never ran.
        if (result.Failure is { } failure)
            return "The debugging session did not start. " + failure;

        return result.State is { } state
            ? DebugStateFormatter.Format(state, root()) + budget.Trailer
            : "The program ran to completion without stopping. No breakpoint was hit — check that the "
            + "breakpoint is on a line the run actually reaches.";
    }

    private async Task<string> ResumeAsync(DebugStepKind? step, CancellationToken ct)
    {
        if (!budget.TryConsume()) return budget.ExhaustedMessage;

        var state = step is null
            ? await session.ContinueAsync(ct)
            : await session.StepAsync(step.Value, ct);

        return state is null
            ? "The program ended (no further stop). Start a new session if you need to observe it again."
            : DebugStateFormatter.Format(state, root()) + budget.Trailer;
    }

    private async Task<string> StopAsync(CancellationToken ct)
    {
        await session.StopAsync(ct);
        return "Debugging session stopped.";
    }
}
