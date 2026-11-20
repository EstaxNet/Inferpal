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
/// <b>Consent, decided before this file existed.</b> <c>start</c> executes the user's
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
    Func<string> root,
    AgentBreakpoints? agentBreakpoints = null) : ITool
{
    /// <summary>What this tool added; a breakpoint it did not add is the user's, and it stays.</summary>
    private readonly AgentBreakpoints _agent = agentBreakpoints ?? (session as AgentCleaningDebugSession)?.Agent ?? new();

    /// <summary>The highest pass a breakpoint may wait for.</summary>
    internal const int MaxHitCount = 1_000_000;

    /// <summary>The place in the debug output the next result reads the trace from: each shows only what came since.
    /// Kept in the session's own record (<see cref="AgentBreakpoints.TraceStart"/> marks where it began).</summary>
    private string? _traceSince;

    public const string ToolName = "debug_control";

    public string Name => ToolName;

    /// <inheritdoc/>
    public bool ActsOnTheEditorOrIndex => true;
    /// <inheritdoc/>
    /// <remarks>Listing the breakpoints reads; setting or clearing one changes the debugger; starting, resuming, stepping
    /// and stopping run the program — and so does a breakpoint with a condition or a message, whose expressions the
    /// debugger evaluates in the program at each pass.</remarks>
    public ToolEffect EffectOf(JsonElement args) => args.Keyword("action") switch
    {
        "list_breakpoints"                     => ToolEffect.Reads,
        "set_breakpoint" when RunsCode(args)   => ToolEffect.Runs,
        "set_breakpoint" or "clear_breakpoint" => ToolEffect.Writes,
        _                                      => ToolEffect.Runs,
    };

    /// <summary>A breakpoint whose condition or message is an expression the program evaluates.</summary>
    private static bool RunsCode(JsonElement args) => args.Trimmed("condition") is not null || args.Trimmed("log") is not null;

    public string Description =>
        "Drives the debugger: set or clear breakpoints, start a debugging session, resume, and step. "
        + "Breakpoints you set are removed when the session stops; the user's own are never removed. "
        + "Starting a session runs the user's program and asks them for confirmation first. "
        + "Use this to test a hypothesis about runtime behaviour instead of guessing from the source: "
        + "set a breakpoint where the state matters, start, then read the state with debug_inspect. "
        + "A breakpoint can stop only when a condition holds (condition), only at its Nth pass (hit_count), or write a "
        + "message at each pass without stopping (log, a tracepoint): what tracepoints wrote comes back with the next "
        + "start, continue or step, and from get_debugger_state. "
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
            condition = new { type = "string", description = "set_breakpoint: stop only when this expression, in the program's language, is true (optional)." },
            hit_count = new { type = "integer", description = $"set_breakpoint: stop only at this pass, 1-{MaxHitCount} (optional)." },
            log = new { type = "string", description = "set_breakpoint: write this message at each pass WITHOUT stopping — a tracepoint; {expression} is replaced by its value (optional)." },
        },
        required = new[] { "action" },
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (!session.IsAvailable)
            return "No debugger is reachable from this editor session. Do not retry; reason from the "
                 + "source instead, or ask the user to run the program.";

        var action = args.Keyword("action");
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
        if (args.Str("file") is not { Length: > 0 } raw)
            return (null, 0, "Error: 'file' is required for this action.");

        // ToolArgs: "line": "14" is a common model form, and TryGetInt32 threw on it.
        var line = args.Int("line", 0);
        if (line < 1)
            return (null, 0, "Error: 'line' is required for this action and must be a 1-based line number.");

        var workspace = root();
        var full = PathSanitizer.Sanitize(raw, workspace);
        try { PathSanitizer.AssertUnderRoot(full, workspace); }
        catch (Exception ex) { return (null, 0, $"Error: {ex.Message}"); }

        return (full, line, null);
    }

    private async Task<string> SetBreakpointAsync(JsonElement args, CancellationToken ct)
    {
        var (file, line, error) = ReadLocation(args);
        if (error is not null) return error;

        // ⚠ A hit count left empty, null or 0 is no hit count: models fill unused optional numbers, and read as "pass 1"
        // the breakpoint stops once and the run then "ends" — a loop that seems to run once. A negative one is clamped, said.
        int? hitCount = null;
        string? clampNote = null;
        if (args.Int("hit_count", 0) != 0)
        {
            var (value, notice) = ClampedArgument.Read(args, "hit_count", 1, 1, MaxHitCount);
            (hitCount, clampNote) = (value, notice);
        }
        var options = new BreakpointOptions(args.Trimmed("condition"), hitCount, args.Trimmed("log"));

        // A breakpoint already there is used as it is: added again it is a duplicate, and adopted it would be removed
        // with the assistant's own when the session stops. ⚠ A disabled one never stops the program: told it "will be hit",
        // the model reads a run that goes past it as the line never running.
        if (await ExistingAtAsync(file!, line, ct) is { } existing)
        {
            var whose = _agent.Owns(file!, line)
                ? "It is yours, and it is removed when the session stops."
                : "It is the user's, and it stays when the session stops.";
            // ⚠ What was asked beyond the place is NOT applied to it: said, or the model reads every stop as its condition.
            var notApplied = options.IsEmpty ? string.Empty
                : " The condition, hit count or message you asked for was NOT applied: it stays as it is"
                  + (_agent.Owns(file!, line) ? " — clear it and set it again to change it." : ".");
            return (existing.Enabled
                ? $"A breakpoint is already set at {file}:{line}; it will be hit as it is. {whose}"
                : $"A breakpoint is already set at {file}:{line}, but it is DISABLED: the program will not stop there. {whose} "
                  + "Ask the user to enable it. A run that does not stop there says nothing about whether the line runs.")
                + notApplied;
        }

        // ⚠ A condition or a message is code the program runs at each pass — a method call is a valid condition: asked like
        // an evaluation, the expressions as the subject the permission rules match. The place alone asks nothing.
        if (options.TrimmedCondition is not null || options.MarkedLog is not null)
        {
            var expressions = string.Join("  |  ", new[] { options.TrimmedCondition, options.Log?.Trim() }.Where(e => e is not null));
            if (!await approval.RequestApprovalAsync(ToolName, Strings.DebugBreakpointCodeConfirm($"{file}:{line}", expressions),
                                                     ct, subject: expressions))
                return Strings.DebugBreakpointCodeCancelled;
        }

        // The first tracepoint of a run marks where its trace begins — before it is set: a hot loop writes during the round
        // trip. One set after the last was removed starts a new trace: what came before belongs to a run that is over.
        if (options.MarkedLog is not null && !_agent.HasTracepoints) await MarkTraceStartAsync(ct);
        var bp = await session.AddBreakpointAsync(file!, line, options.IsEmpty ? null : options, ct);
        if (bp is null)
            return $"The debugger refused a breakpoint at {file}:{line} (no executable code on that line?). "
                 + "Try the first executable line of the statement.";
        _agent.Track(bp.File, bp.Line, tracepoint: bp.Log is not null);
        return ClampedArgument.Above(clampNote, Describe(bp, options));
    }

    /// <summary>The breakpoint as the debugger took it — what it kept of the condition, the pass and the message read
    /// back from it, never echoed from the request: a debugger that dropped one stops at every pass, and the model must
    /// know.</summary>
    private static string Describe(DebugBreakpointInfo bp, BreakpointOptions asked)
    {
        var where = $"{bp.File}:{bp.Line}";
        var when = string.Concat(
            bp.Condition is { } c ? $" when `{c}` is true" : string.Empty,
            bp.HitCount is { } n ? $" at pass {n}" : string.Empty);
        var head = bp.Log is { } log
            ? $"Tracepoint set at {where}: it writes \"{log.Replace(BreakpointOptions.TraceMarker, string.Empty)}\"{(when.Length > 0 ? when : " at each pass")} "
              + "WITHOUT stopping; what it writes comes back with the next start, continue or step."
            : $"Breakpoint set at {where}{(when.Length > 0 ? ": it stops only" + when : string.Empty)}.";
        var dropped = new List<string>();
        if (!string.IsNullOrWhiteSpace(asked.Condition) && bp.Condition is null) dropped.Add("the condition");
        if (asked.HitCount is not null && bp.HitCount is null) dropped.Add("the hit count");
        if (!string.IsNullOrWhiteSpace(asked.Log) && bp.Log is null) dropped.Add("the message (it STOPS at each pass)");
        var lost = dropped.Count == 0 ? string.Empty
            : $" The debugger did not keep {string.Join(", ", dropped)}: treat it as a breakpoint without {(dropped.Count == 1 ? "it" : "them")}.";
        // ⚠ Neither debugger checks a condition when it is set: one that cannot be evaluated stops the program (Visual
        // Studio) or is skipped with an error in the console — read as "the condition held" or "the line never ran".
        var notChecked = bp.Condition is null ? string.Empty
            : " The debugger does not check the condition until it runs: if the program stops here while it looks false, "
              + "or never stops, evaluate it with debug_inspect at a stop.";
        return head + lost + notChecked + " It is removed when the session stops.";
    }

    /// <summary>Marks where the debug output ends now: this run's trace starts there, and the next result reads from it.
    /// A debugger that cannot say leaves the start empty — the output it holds, from its start, where an earlier run's
    /// lines may show (a debugger that answers no mark rarely answers the reads that follow).</summary>
    private async Task MarkTraceStartAsync(CancellationToken ct)
    {
        string? end = null;
        try { end = (await session.ReadTraceAsync(null, ct)).Next; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Diagnostics.Swallow("DebugControlTool.TraceStart", ex); }
        _agent.TraceStart = _traceSince = end ?? string.Empty;
    }

    /// <summary><paramref name="result"/>, then the lines the assistant's tracepoints wrote since the last result read
    /// them — read only when <paramref name="tracing"/>: a tracepoint of the assistant was set when the action began (a run
    /// that ends removes them, and its last lines are the ones that matter). A trace that cannot be read is said, never
    /// left out in silence.</summary>
    private async Task<string> WithTraceAsync(string result, bool tracing, CancellationToken ct)
    {
        if (!tracing) return result;
        TraceRead read;
        try { read = await session.ReadTraceAsync(_traceSince, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("DebugControlTool.ReadTrace", ex);
            return result + "\n\nThe tracepoints' output could not be read: " + ex.Message;
        }
        _traceSince = read.Next ?? _traceSince;
        return result + "\n\n" + (read.Lines.Count == 0 && !read.Cut
            ? "Tracepoints: nothing written since the last result."
            : DebuggerStateReader.TraceSection(read));
    }

    private async Task<string> ClearBreakpointAsync(JsonElement args, CancellationToken ct)
    {
        var (file, line, error) = ReadLocation(args);
        if (error is not null) return error;

        if (!_agent.Owns(file!, line))
            return await ExistingAtAsync(file!, line, ct) is not null
                ? $"The breakpoint at {file}:{line} was not set by you — it is the user's, and it stays."
                : $"No breakpoint at {file}:{line}.";

        _agent.Untrack(file!, line);
        return await session.RemoveBreakpointAsync(file!, line, ct)
            ? $"Breakpoint cleared at {file}:{line}."
            : $"No breakpoint at {file}:{line}.";
    }

    /// <summary>The breakpoint at the line — an enabled one first: an editor may hold several on one line (columns).</summary>
    private async Task<DebugBreakpointInfo?> ExistingAtAsync(string file, int line, CancellationToken ct) =>
        (await session.ListBreakpointsAsync(ct)).Where(b => AgentBreakpoints.Same(b.File, b.Line, file, line))
            .OrderByDescending(b => b.Enabled).FirstOrDefault();

    private async Task<string> ListBreakpointsAsync(CancellationToken ct)
    {
        var all = await session.ListBreakpointsAsync(ct);
        if (all.Count == 0) return "No breakpoints are set.";

        return "Breakpoints:\n" + string.Join('\n', all.Select(b =>
            $"- {b.File}:{b.Line}{(b.Enabled ? string.Empty : " (disabled)")}"
            + (b.Condition is { } c ? $" when `{c}`" : string.Empty)
            + (b.HitCount is { } n ? $" at pass {n}" : string.Empty)
            + (b.Log is { } log ? $" — tracepoint, writes \"{log.Replace(BreakpointOptions.TraceMarker, string.Empty)}\" without stopping" : string.Empty)));
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
        // A new run's trace starts here: what an earlier run wrote is not this one's (VS Code keeps the console across
        // sessions; Visual Studio empties its pane, and a place it no longer holds reads it from its start).
        var tracing = _agent.HasTracepoints;
        if (tracing) await MarkTraceStartAsync(ct);
        var result = await session.StartAsync(ct);

        // Three outcomes, deliberately not two: "it could not start" must never be dressed up as
        // "your breakpoint was never reached", which would send the agent hunting a bug in code that
        // never ran.
        if (result.Failure is { } failure)
            return ToolFailure.Said("The debugging session did not start. " + failure);

        if (result.StillRunning)
            return await WithTraceAsync(
                $"The program started and is still running under the debugger: it did not reach a stop within "
                + $"{(int)DebugOps.StartBudget.TotalMinutes} minute(s). The session is live and your breakpoints stay set — "
                + "it may be waiting for input or a request, or the breakpoint is on a line the run has not reached. "
                + "`get_debugger_state` shows a later stop; `stop` ends the session.", tracing, ct);

        return await WithTraceAsync(result.State is { } state
            ? DebugStateFormatter.Format(state, root()) + budget.Trailer
            : tracing
            // Tracepoints do not stop the program: a run that ends is what they are for, not a breakpoint missed.
            ? "The program ran to completion without stopping (tracepoints write without stopping). The session is over: "
              + "the breakpoints you set were removed, set them again before the next start."
            : "The program ran to completion without stopping. No breakpoint was hit — check that the "
            + "breakpoint is on a line the run actually reaches. The session is over: the breakpoints you set were "
            + "removed, set them again before the next start.", tracing, ct);
    }

    private async Task<string> ResumeAsync(DebugStepKind? step, CancellationToken ct)
    {
        if (!budget.TryConsume()) return budget.ExhaustedMessage;

        // Through the registry's session, an ended run has already taken the breakpoints set here with it; a bare
        // session (a test, a front-end wired without the registry) gets the same cleanup from the tool itself.
        var owned  = _agent.Snapshot().Count;
        var tracing = _agent.HasTracepoints;
        var result = step is null
            ? await session.ContinueAsync(ct)
            : await session.StepAsync(step.Value, ct);

        switch (result.Outcome)
        {
            case DebugResumeOutcome.Stopped when result.State is { } state:
                return await WithTraceAsync(DebugStateFormatter.Format(state, root()) + budget.Trailer, tracing, ct);

            case DebugResumeOutcome.Ended:
            {
                var removed = session is AgentCleaningDebugSession ? owned : await _agent.RemoveAllAsync(session, ct);
                return await WithTraceAsync("The program ran to completion: no further stop. The session is over"
                     + (removed == 0 ? "." : $", and the {removed} breakpoint(s) you set were removed.")
                     + " Start it again to look at another run.", tracing, ct);
            }

            case DebugResumeOutcome.StillRunning:
                return await WithTraceAsync(
                    $"The program is still running under the debugger: no stop within {(int)DebugOps.ResumeBudget.TotalMinutes} "
                    + "minute(s). The session is live and your breakpoints stay set — it may be waiting for input, or the "
                    + "next breakpoint is on a line the run has not reached. `get_debugger_state` shows a later stop; "
                    + "`stop` ends the session.", tracing, ct);

            case DebugResumeOutcome.NotPaused:
                return "No debugging session is paused, so there was nothing to resume. `start` begins one; "
                     + "`get_debugger_state` says what the debugger is doing.";

            default:
                return "The program was not resumed. " + (result.Failure ?? "The debugger gave no reason.")
                     + " Nothing is known about the program's state from this call.";
        }
    }

    private async Task<string> StopAsync(CancellationToken ct)
    {
        // Through the registry's session, stopping also removes the breakpoints set here; a bare session (a test,
        // a front-end wired without the registry) gets the same cleanup from the tool itself.
        var removed = session is AgentCleaningDebugSession ? _agent.Snapshot().Count : await _agent.RemoveAllAsync(session, ct);
        await session.StopAsync(ct);
        return removed == 0
            ? "Debugging session stopped."
            : $"Debugging session stopped, and the {removed} breakpoint(s) you set were removed.";
    }
}
