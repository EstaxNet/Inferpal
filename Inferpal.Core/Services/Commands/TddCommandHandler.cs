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
    public static async Task<TddCommandResult> HandleAsync(
        IInferenceProvider client, InferpalConfig config, IToolRegistry tools,
        string? systemPrompt, string[] parts, string? projectRoot,
        Action<string>? onProgress, Action<string, bool>? onTestReport,
        Action<string>? onStep, Action<string>? onToken, Action<string>? onFixResult,
        CancellationToken ct)
    {
        var filter = parts.Length >= 2 ? string.Join(" ", parts[1..]) : null;

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

            // ── Fix iteration: the agent reads the failures and patches the code ──
            ct.ThrowIfCancellationRequested();
            onProgress?.Invoke(Strings.TddFixing(round));

            var history = new List<ChatMessageDto>();
            if (!string.IsNullOrWhiteSpace(systemPrompt)) history.Add(new("system", systemPrompt));
            history.Add(new("user", BuildFixPrompt(output, filter)));

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
    internal static string BuildFixPrompt(string testReport, string? filter)
    {
        var scope = string.IsNullOrWhiteSpace(filter)
            ? "the failing tests"
            : $"the failing tests matched by the filter '{filter}'";
        return
            $"The test suite has failures. Make {scope} pass.\n\n" +
            "Test report:\n```\n" + testReport.Trim() + "\n```\n\n" +
            "Rules:\n" +
            "- Read the involved code first (read_file / search_in_files), then apply the smallest " +
            "fix with apply_diff or apply_edits.\n" +
            "- Fix the production code. Only change a test if it is plainly wrong, and say so.\n" +
            "- Do NOT run the tests yourself; the caller re-runs them after your changes.\n" +
            "- If the failure cannot be fixed from the report, explain what is missing instead of guessing.";
    }
}
