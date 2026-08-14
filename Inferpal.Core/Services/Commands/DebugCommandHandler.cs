using System.Text;
using Inferpal.Localization;
using Inferpal.Services.Debugging;

namespace Inferpal.Services.Commands;

/// <summary>
/// Pure logic of <c>/debug</c> — the hypothesis loop of roadmap §21: instead of guessing what the
/// running program does, set a breakpoint, look, and ask again.
/// </summary>
/// <remarks>
/// <para>
/// The command itself drives nothing. It reports what the debugger currently is, or it hands the
/// model a framed instruction to run with <c>debug_control</c> and <c>debug_inspect</c> — which is
/// what makes the loop go through the ordinary tool path, with its approval on the one action that
/// executes. A command that started sessions itself would be a second way in, next to the one the
/// consent model was written for.
/// </para>
/// <para>
/// Shared by both front-ends, like every other command handler: the Visual Studio view-model and the
/// host call this and apply the result. The debugger differs underneath — EnvDTE on one side, the
/// Debug Adapter Protocol on the other — and nothing here can tell.
/// </para>
/// </remarks>
internal static class DebugCommandHandler
{
    /// <param name="Message">Markdown to display, when the command answers by itself.</param>
    /// <param name="SendAsPrompt">
    /// A framed instruction to run as an ordinary agent turn. The tools do the work and their
    /// approvals still apply; this is only the wording that starts the loop.
    /// </param>
    internal readonly record struct DebugCommandResult(string? Message, string? SendAsPrompt = null);

    /// <param name="session">The front-end's debugger port; <c>null</c> when it has none.</param>
    /// <param name="parts">Tokenised command — everything after <c>/debug</c> is the hypothesis.</param>
    public static async Task<DebugCommandResult> HandleAsync(
        IDebugSession? session, string[] parts, CancellationToken ct)
    {
        if (session is null || !session.IsAvailable)
            return new(Strings.DebugUnavailable);

        var sub = parts.Length >= 2 ? parts[1].Trim() : string.Empty;

        if (sub.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            await session.StopAsync(ct);
            return new(Strings.DebugStopped);
        }

        // Bare `/debug` answers the only question worth asking before starting: what is the
        // debugger actually doing right now?
        if (sub.Length == 0 || sub.Equals("status", StringComparison.OrdinalIgnoreCase))
            return new(await StatusAsync(session, ct));

        if (sub.Equals("help", StringComparison.OrdinalIgnoreCase))
            return new(Strings.DebugUsage);

        return new(null, SendAsPrompt: BuildLoopPrompt(string.Join(" ", parts[1..])));
    }

    private static async Task<string> StatusAsync(IDebugSession session, CancellationToken ct)
    {
        var sb = new StringBuilder(Strings.DebugStatusHeader).Append("\n\n");

        var state = await session.GetStateAsync(ct);
        sb.Append(state is null
            ? Strings.DebugStatusNotPaused
            : Strings.DebugStatusPaused(TopFrame(state))).Append('\n');

        var breakpoints = await session.ListBreakpointsAsync(ct);
        if (breakpoints.Count == 0)
        {
            sb.Append(Strings.DebugStatusNoBreakpoints).Append('\n');
        }
        else
        {
            sb.Append('\n').Append(Strings.DebugStatusBreakpoints).Append('\n');
            foreach (var b in breakpoints.Take(20))
                sb.Append("- `").Append(b.File).Append(':').Append(b.Line).Append('`')
                  .Append(b.Enabled ? string.Empty : " (disabled)").Append('\n');
            if (breakpoints.Count > 20)
                sb.Append("- … ").Append(breakpoints.Count - 20).Append('\n');
        }

        sb.Append('\n').Append(Strings.DebugUsage);
        return sb.ToString().TrimEnd();
    }

    /// <summary>Top user frame, or the stop reason when the stack says nothing useful.</summary>
    private static string TopFrame(DebugStopState state)
    {
        if (state.Frames.Count == 0) return state.Reason;
        var top = state.Frames[0];
        return top.File is null ? top.Function : $"{top.Function} ({top.File}:{top.Line})";
    }

    /// <summary>
    /// The instruction that turns a question into a loop. English on purpose — model-facing, like
    /// the agent system prompt and <c>/tdd</c>'s fix prompt; only the UI strings are localized.
    /// </summary>
    /// <remarks>
    /// Every line here answers a failure mode the §21 probe or the two tools' own rules made
    /// visible: a session must be started before anything can be read, values are the debugger's
    /// own rendering and mean nothing when paraphrased, the step budget is finite and running out
    /// is a result to report rather than a reason to conclude, and a run left paused blocks the
    /// user's IDE.
    /// </remarks>
    internal static string BuildLoopPrompt(string hypothesis) =>
        $"Investigate this at runtime rather than by reading the source: {hypothesis.Trim()}\n\n"
      + "Method:\n"
      + "1. Find the code involved (search_in_files / read_file) and pick the one place where the "
      + "state would settle the question.\n"
      + "2. Set a breakpoint there with debug_control, then start the session — starting runs the "
      + "user's program and they will be asked to confirm it.\n"
      + "3. Read the stop with debug_inspect: stack, locals, and evaluate the expressions that "
      + "discriminate between your hypotheses. Step or continue only when you know what the next "
      + "step is meant to show.\n"
      + "4. Report what the program actually did. Quote the values you read verbatim — they are "
      + "rendered by the debugger and mean nothing rephrased.\n\n"
      + "Rules:\n"
      + "- If the session cannot start, say so and stop; do not present unverified reasoning as an "
      + "observation.\n"
      + "- If the step budget runs out, report what you established and what is still open — an "
      + "unfinished investigation is a result, an invented conclusion is not.\n"
      + "- Stop the session with debug_control action='stop' when you are done, so the user's IDE "
      + "is not left paused.";
}
