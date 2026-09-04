using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;

namespace Inferpal.Services.Commands;

/// <summary>
/// Pure logic of <c>/tdd [filter]</c> — the "fix until green" loop (ROADMAP 1.5.0 §10, pulled
/// forward): <c>run_tests</c> → agent patches the code → re-run, until the suite is green or the
/// iteration budget runs out. The twin of <c>/fix-build</c> on the test side, but shared by both
/// front-ends: the VM and the Host drive it through callbacks (status line, per-round test report,
/// streamed fix tokens) and the handler owns the loop. Mutating tools still go through the usual
/// approval/permission pipeline of the registry — the loop adds no bypass.
/// </summary>
internal static class TddCommandHandler
{
    /// <summary>Outcome of a <c>/tdd</c> run: the final markdown to display.</summary>
    internal readonly record struct TddCommandResult(string Message);

    /// <summary>Iteration budget — same as <c>/fix-build</c>'s (its twin).</summary>
    internal const int MaxRounds = 5;

    /// <param name="client">Inference provider; the fix iterations use the agent-role model.</param>
    /// <param name="config">Read for the agent model resolution only.</param>
    /// <param name="tools">Registry executing <c>run_tests</c> and the agent's fix tools.</param>
    /// <param name="systemPrompt">First message of each fix run (context + rules); null = none.</param>
    /// <param name="parts">Tokenised command; everything after <c>/tdd</c> is the test filter.</param>
    /// <param name="projectRoot">Passed as <c>run_tests</c>'s <c>path</c> so the runner does not
    /// depend on the process CWD (wrong under the VS host); null/empty = tool default.</param>
    /// <param name="onProgress">Status line ("Tests 2/5…", "Fix 2…").</param>
    /// <param name="onTestReport">Per-round test report (output, green) — chat bubble hook.</param>
    /// <param name="onStep">Agent step forwarding during fix iterations.</param>
    /// <param name="onToken">Agent token stream during fix iterations.</param>
    /// <param name="onFixResult">Agent's final text per fix iteration (raw — may contain think
    /// tags; the front-end strips/renders).</param>
    /// <param name="debugCapture">§25 port: re-runs the first failing test under the editor's
    /// debugger so the fix prompt carries the state at the failure point. Null (or unavailable)
    /// = the historical loop, unchanged — the block is a bonus, never a prerequisite.</param>
    /// <param name="approval">Gate for <paramref name="debugCapture"/>: re-running a test is an
    /// execution. Asked once per `/tdd` run (the §21 consent granularity is the session, not the
    /// step); a refusal disables the capture for the rest of the run without stopping it.</param>
    public static async Task<TddCommandResult> HandleAsync(
        IInferenceProvider client, InferpalConfig config, IToolRegistry tools,
        string? systemPrompt, string[] parts, string? projectRoot,
        Action<string>? onProgress, Action<string, bool>? onTestReport,
        Action<string>? onStep, Action<string>? onToken, Action<string>? onFixResult,
        CancellationToken ct,
        Debugging.ITestDebugCapture? debugCapture = null, IApprovalService? approval = null)
    {
        var filter = parts.Length >= 2 ? string.Join(" ", parts[1..]) : null;
        bool captureApproved = false, captureDeclined = false, captureAbsenceAnnounced = false;

        // §25: writes aimed at test files are force-prompted for the whole run — the loop's most
        // natural failure mode is rewriting the assertion to the observed buggy value. The sibling
        // shares the parent's FileHistoryService, and the run opened here groups every fix-round
        // write so /undo-run can revert the whole /tdd session (pre-1.6.0 architecture review, §1.5).
        IDisposable? run = null;
        if (tools is ToolRegistry concrete)
        {
            tools = concrete.WithApprovalService(new TestFileWriteGuard(concrete.Approval));
            run   = concrete.History.BeginRunScope();
        }
        // ⚠ The run closes on EVERY exit path — the loop has eight. Without this it stayed the
        // "current" one after the command, and a later write attached to it: /undo-run reverted
        // that write along with the /tdd session.
        using var runScope = run;

        var argsObj = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(projectRoot)) argsObj["path"]   = projectRoot!;
        if (!string.IsNullOrWhiteSpace(filter))      argsObj["filter"] = filter!;
        var runTestsArgs = JsonSerializer.SerializeToElement(argsObj);

        for (int round = 1; round <= MaxRounds; round++)
        {
            ct.ThrowIfCancellationRequested();
            onProgress?.Invoke(Strings.TddRunningTests(round, MaxRounds));

            string output;
            try { output = await tools.ExecuteAsync("run_tests", runTestsArgs, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return new(Strings.MsgError(ex.Message)); }

            // No runner / unusable report → surface the tool's own explanation, don't loop on it.
            if (!LooksLikeTestReport(output)) return new(output.Trim());

            bool green = TestsPassed(output);
            onTestReport?.Invoke(output, green);

            if (green)              return new(Strings.TddSuccess(round));
            if (round == MaxRounds) return new(Strings.TddGiveUp(MaxRounds));

            // ── §25: state at the failure point, captured under the editor's debugger ──
            // Measured before being built (probe gate 12/12 vs 10/12): on the cases the plain
            // loop loses, the runner text never carries the cause — the locals do.
            string? debugBlock = null;

            // A missing capability is announced, once, before the first round it degrades.
            // Staying silent here was measured to cost an evening: the in-process driver had not
            // started, `/tdd` ran its bare red loop, and nothing - neither the chat nor the probe
            // reading it - could tell that apart from a model that fails to fix the code. Same
            // rule as the failed capture two blocks below: never a silent fallback that passes a
            // degraded round off as an ordinary one. `captureDeclined` is excluded: a refusal is
            // a user decision, not a failure, and they already read it on their card.
            //
            // ⚠ An ABSENT port (`null`) stays quiet: that is a front-end with no debugger at all
            // (VS Code without the §21 `debug` capability), not a failure. Announcing a missing
            // capability where it never existed is noise on every `/tdd`.
            if (debugCapture is { IsAvailable: false } && !captureAbsenceAnnounced)
            {
                captureAbsenceAnnounced = true;
                onProgress?.Invoke(Strings.TddDebugCaptureUnavailable);
            }

            if (debugCapture is { IsAvailable: true } && approval is not null && !captureDeclined)
            {
                var failing = FirstFailingTest(output);
                if (failing is not null)
                {
                    if (!captureApproved)
                    {
                        captureApproved = await approval.RequestApprovalAsync(
                            "debug_test", Strings.TddDebugCaptureApproval(failing), ct, subject: failing);
                        if (!captureApproved) captureDeclined = true;
                    }
                    if (captureApproved)
                    {
                        onProgress?.Invoke(Strings.TddDebugCapturing);
                        var state = await debugCapture.CaptureAsync(failing, projectRoot ?? string.Empty, ct);
                        if (state is not null)
                            debugBlock =
                                $"Debugger state captured at the failure point of {failing} " +
                                "(the debugger paused on the thrown exception; values were read live):\n\n" +
                                Debugging.DebugStateFormatter.Format(state, projectRoot);
                        else
                            // A failed capture says so (probe lesson: no silent fallback that
                            // would make a degraded round look like a plain one).
                            onProgress?.Invoke(Strings.TddDebugCaptureFailed);
                    }
                }
            }

            // ── Fix iteration: the agent reads the failures and patches the code ──
            ct.ThrowIfCancellationRequested();
            onProgress?.Invoke(Strings.TddFixing(round));

            var history = new List<ChatMessageDto>();
            if (!string.IsNullOrWhiteSpace(systemPrompt)) history.Add(new("system", systemPrompt));
            history.Add(new("user", BuildFixPrompt(output, filter, debugBlock)));

            // RunAgentAsync never throws (network errors come back inside AgentResult);
            // only cancellation propagates, which is exactly what we want here.
            var fix = await client.RunAgentAsync(
                ModelRouter.Resolve(config, ModelRole.Agent), history, tools,
                onStep:  step  => onStep?.Invoke(step),
                onToken: token => onToken?.Invoke(token),
                ct:      ct);
            if (!string.IsNullOrWhiteSpace(fix.FinalResponse)) onFixResult?.Invoke(fix.FinalResponse);
        }

        return new(Strings.TddGiveUp(MaxRounds));   // unreachable, keeps the compiler honest
    }

    /// <summary>
    /// Green iff the report's verdict line is a pass. <see cref="Tools.RunTestsTool"/>'s parsers
    /// emit "✓ …" / "✗ FAILED …" for dotnet, cargo and go; pytest keeps its own summary line
    /// ("5 passed in 0.2s" / "1 failed, 4 passed in 0.3s"), matched textually. Anything
    /// unrecognisable counts as failing — one wasted agent round beats a false green.
    /// </summary>
    internal static bool TestsPassed(string output)
    {
        var t = output.TrimStart();
        if (t.StartsWith('✓')) return true;
        if (t.StartsWith('✗')) return false;
        var verdictLine = t.Split('\n')[0];
        return System.Text.RegularExpressions.Regex.IsMatch(verdictLine, @"\b\d+ passed\b")
            && !System.Text.RegularExpressions.Regex.IsMatch(verdictLine, @"\b\d+ (failed|error)");
    }

    /// <summary>Only "no runner detected" is a non-report — iterating on it is pointless. The
    /// tool's raw fallback dumps still carry the failure text the agent needs, so they loop.</summary>
    internal static bool LooksLikeTestReport(string output) =>
        !output.TrimStart().StartsWith("No test runner detected", StringComparison.OrdinalIgnoreCase);

    /// <summary>Fix-iteration prompt. English on purpose — model-facing, like the agent system
    /// prompt and the bench tasks; only UI strings are localized.</summary>
    /// <param name="debuggerBlock">§25 state block, inserted <b>before</b> the Rules section —
    /// measured, not a preference: appended after the rules, the model narrated its diagnosis
    /// round after round without ever calling apply_diff (probe, 2026-08-20). Both variants of
    /// the prompt must end on the same action rules.</param>
    internal static string BuildFixPrompt(string testReport, string? filter, string? debuggerBlock = null)
    {
        var scope = string.IsNullOrWhiteSpace(filter)
            ? "the failing tests"
            : $"the failing tests matched by the filter '{filter}'";
        return
            $"The test suite has failures. Make {scope} pass.\n\n" +
            "Test report:\n```\n" + testReport.Trim() + "\n```\n\n" +
            (string.IsNullOrEmpty(debuggerBlock) ? "" : debuggerBlock.TrimEnd() + "\n\n") +
            "Rules:\n" +
            "- Read the involved code first (read_file / search_in_files), then apply the smallest " +
            "fix with apply_diff or apply_edits.\n" +
            "- Fix the production code. Only change a test if it is plainly wrong, and say so.\n" +
            // The two lines below exist because of a measured failure mode, not caution: in the
            // §25 gate pass, three different models read the debugger block as the truth to
            // encode and rewrote the assertion to the observed buggy value (7.5, then 6.0).
            "- NEVER rewrite an assertion to match the observed values: the report and the " +
            "debugger state show the BUGGY behaviour, not the expected one. The expected values " +
            "in the test are the specification.\n" +
            "- Do NOT run the tests yourself; the caller re-runs them after your changes.\n" +
            "- If the failure cannot be fixed from the report, explain what is missing instead of guessing.";
    }

    /// <summary>
    /// First "  ✗ Full.Test.Name" entry of a <c>run_tests</c> dotnet report — the test the §25
    /// capture re-runs. The summary line ("✗ FAILED — …") is excluded, and a name without a dot
    /// is not a test FQN. Null on non-dotnet reports (pytest/cargo/go names are not re-runnable
    /// by the .NET capture recipes) — the loop then simply runs without a block.
    /// </summary>
    internal static string? FirstFailingTest(string report) =>
        report.Split('\n')
              .Select(l => l.Trim())
              .Where(l => l.StartsWith("✗ ") && !l.StartsWith("✗ FAILED"))
              .Select(l => l[2..].Trim())
              .FirstOrDefault(n => n.Contains('.') && !n.Contains(' '));
}
