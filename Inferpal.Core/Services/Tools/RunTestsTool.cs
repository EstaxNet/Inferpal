using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Tools;

internal class RunTestsTool : ITool
{
    private const int DefaultTimeoutSeconds = 120;
    private const int MaxRawChars           = 6000;

    private readonly Func<string?> _getRoot;

    /// <param name="getRoot">The workspace root: where tests run when no path is given, and what a
    /// relative path resolves against. ⚠ Never the process's working directory, which in Visual
    /// Studio is the out-of-process host's folder — "No test runner detected" on a solution full of
    /// tests, which the model reads as "there are no tests".</param>
    public RunTestsTool(Func<string?>? getRoot = null) => _getRoot = getRoot ?? (() => null);

    public string Name => "run_tests";

    public string Description =>
        "Runs the test suite and returns a summary (passed/failed/skipped) with error details for each failure. " +
        "Supports dotnet test (.sln/.csproj), pytest (Python), npm test (Node.js), cargo test (Rust), and go test (Go). " +
        "The runner is auto-detected from project files; set 'runner' to force one. " +
        "Use 'filter' to run a specific test or class. " +
        "Typical workflow: fix code with apply_diff, then call run_tests to verify.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path = new
            {
                type        = "string",
                description = "Path to a project file (.sln/.csproj), directory, or test file. Optional, defaults to the workspace root."
            },
            filter = new
            {
                type        = "string",
                description = "Test name filter. dotnet: --filter expression (e.g. 'FullyQualifiedName~MyTest'). pytest: -k expression. npm/jest: --testNamePattern. cargo: substring filter. go: -run regexp."
            },
            runner = new
            {
                type        = "string",
                description = "Force a runner: 'dotnet', 'pytest', 'npm', 'cargo', or 'go'. Default: 'auto' (detected from project files)."
            },
            timeout_seconds = new
            {
                type        = "integer",
                description = $"Max seconds to wait. Default: {DefaultTimeoutSeconds}."
            }
        },
        required = Array.Empty<string>(),
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var root    = _getRoot();
        var rawPath = args.Trimmed("path");
        var path    = string.IsNullOrWhiteSpace(rawPath) ? null : PathSanitizer.Sanitize(rawPath, root);
        var filter  = args.Trimmed("filter");
        var forced  = args.Keyword("runner");
        var timeout = args.Int("timeout_seconds", DefaultTimeoutSeconds);

        var workDir = ResolveWorkDir(path, root);
        var runner  = (forced is null or "auto") ? DetectRunner(workDir, path) : forced;

        // ⚠ The budget is decorated HERE, after the parser, never inside the log it reads: the
        // parsers compose their verdict line from summaries, so a sentence buried in the raw text
        // is dropped, and a killed run reported the pass of the projects that had finished.
        var budget = new RunBudget(timeout);
        var report = runner switch
        {
            "dotnet" => await RunDotnetAsync(workDir, path, filter, budget, ct),
            "pytest" => await RunPytestAsync(workDir, filter, budget, ct),
            "npm"    => await RunNpmAsync(workDir, filter, budget, ct),
            "cargo"  => await RunCargoAsync(workDir, filter, budget, ct),
            "go"     => await RunGoAsync(workDir, filter, budget, ct),
            _        => NoRunnerDetected(workDir, root),
        };
        return budget.Wrap(report);
    }

    // ── Runner implementations ─────────────────────────────────────────────────

    private static async Task<string> RunDotnetAsync(string workDir, string? path, string? filter, RunBudget budget, CancellationToken ct)
    {
        var sb = new StringBuilder("test");
        if (!string.IsNullOrWhiteSpace(path))
            sb.Append($" \"{path}\"");
        sb.Append(" --verbosity normal --nologo");
        if (!string.IsNullOrWhiteSpace(filter))
            sb.Append($" --filter \"{filter}\"");

        var (output, exitCode) = await RunProcessAsync("dotnet", sb.ToString(), workDir, budget, ct);
        return ParseDotnetOutput(output, exitCode);
    }

    private static async Task<string> RunPytestAsync(string workDir, string? filter, RunBudget budget, CancellationToken ct)
    {
        var args = "-m pytest -v --tb=short -q";
        if (!string.IsNullOrWhiteSpace(filter))
            args += $" -k \"{filter}\"";

        var (output, exitCode) = await RunProcessAsync("python", args, workDir, budget, ct);
        return ParsePytestOutput(output, exitCode);
    }

    private static async Task<string> RunNpmAsync(string workDir, string? filter, RunBudget budget, CancellationToken ct)
    {
        if (ResolveNpm(OperatingSystem.IsWindows(), Shell.ShellLauncher.FindOnPath, File.Exists) is not { } npm)
            return NpmNotRunnable;

        var args = new List<string>(npm.Prefix) { "test" };
        if (!string.IsNullOrWhiteSpace(filter))
        {
            args.Add("--");
            args.Add($"--testNamePattern={filter}");
        }
        var (output, exitCode) = await RunProcessAsync(npm.FileName, string.Empty, workDir, budget, ct, args);
        return ParseNpmOutput(output, exitCode);
    }

    /// <summary>What the npm runner answers when npm cannot be launched without a shell.</summary>
    internal const string NpmNotRunnable =
        "npm could not be run without a shell: npm.cmd was not found on PATH with node.exe and " +
        "node_modules\\npm\\bin\\npm-cli.js beside it. Run the tests with run_command instead.";

    /// <summary>The program and leading arguments that run npm WITHOUT a shell; <c>null</c> when that is not possible.</summary>
    /// <remarks>
    /// ⚠ On Windows npm is a batch script (npm.cmd) and CreateProcess only resolves an executable: launched as "npm", the
    /// runner never started there. And launched as npm.cmd it would go through cmd.exe, which interprets the filter —
    /// written by the model, in a tool that asks no approval — so "&amp;", "|" or "%" in it would become commands. npm's
    /// own CLI, run by the node.exe that sits beside npm.cmd in every Node install, takes its arguments as a list.
    /// </remarks>
    internal static (string FileName, string[] Prefix)? ResolveNpm(bool isWindows, Func<string, string?> onPath, Func<string, bool> exists)
    {
        if (!isWindows) return ("npm", []);   // a script with a shebang: exec runs it, no shell in between
        if (onPath("npm.cmd") is not { } cmd || Path.GetDirectoryName(cmd) is not { } dir) return null;
        var node = Path.Combine(dir, "node.exe");
        var cli  = Path.Combine(dir, "node_modules", "npm", "bin", "npm-cli.js");
        return exists(node) && exists(cli) ? (node, [cli]) : null;
    }

    /// <summary>npm's placeholder "test" script ran — the one <c>npm init</c> writes. Nothing to fix.</summary>
    internal const string NoTestScript =
        "⚠ The project's \"test\" script is npm's placeholder (\"no test specified\") — nothing ran. " +
        "That is not a failure to fix.";

    /// <summary>
    /// A verdict line for <c>npm test</c>, from the summary its runner prints — jest, vitest, mocha or node --test — then
    /// the raw output, which carries the failures.
    /// </summary>
    /// <remarks>
    /// ⚠ The output used to be returned raw, its exit code discarded: it starts with "&gt; project@1.0.0 test", so
    /// /tdd — which reads green only from a verdict line — never saw a Node suite pass, and ran its fix rounds on a
    /// suite that passed. A green summary with a failing exit is not green: <c>npm test</c> can chain a linter or a
    /// coverage gate after the tests. And no summary is never green, the rule of every other parser here.
    /// </remarks>
    internal static string ParseNpmOutput(string raw, int exitCode)
    {
        var rawTail = Truncate(raw.Trim(), MaxRawChars);
        if (raw.Contains("Error: no test specified", StringComparison.Ordinal))
            return NoTestScript + "\n\n" + rawTail;

        if (NpmSummary(raw) is not { } s)
            return exitCode == 0 ? NothingProven + "\n\n" + rawTail : rawTail;

        var counts = $"Failed: {s.Failed}, Passed: {s.Passed}, Skipped: {s.Skipped}, Total: {s.Total}";
        var head = s.Failed == 0 && s.Passed == 0 ? NoTestMatchedFilter
                 : s.Failed > 0                   ? $"✗ FAILED — {counts}"
                 : exitCode != 0                  ? $"✗ FAILED — npm test exited with code {exitCode} although its test summary passed ({counts})"
                 :                                  $"✓ PASSED — {counts}";
        return head + "\n\n" + rawTail;
    }

    private readonly record struct NpmCounts(int Failed, int Passed, int Skipped, int Total);

    private static NpmCounts? NpmSummary(string raw)
    {
        static int Num(Match m) => m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        static Match Last(string text, string pattern) =>
            Regex.Matches(text, pattern, RegexOptions.Multiline, RegexBudget.Default) is { Count: > 0 } all ? all[^1] : Match.Empty;

        // jest: "Tests:       1 failed, 2 passed, 3 total" — vitest: "      Tests  1 failed | 1 passed (2)"
        var tests = Last(raw, @"^\s*Tests:?[ \t]+([^\r\n]*\b(?:passed|failed|skipped|total)\b[^\r\n]*)$");
        if (tests.Success)
        {
            var body    = tests.Groups[1].Value;
            var failed  = Num(Regex.Match(body, @"(\d+) failed", RegexOptions.None, RegexBudget.Default));
            var passed  = Num(Regex.Match(body, @"(\d+) passed", RegexOptions.None, RegexBudget.Default));
            var skipped = Num(Regex.Match(body, @"(\d+) (?:skipped|todo)", RegexOptions.None, RegexBudget.Default));
            var total   = Regex.Match(body, @"(\d+) total|\((\d+)\)", RegexOptions.None, RegexBudget.Default) is { Success: true } t
                ? int.Parse(t.Groups[1].Success ? t.Groups[1].Value : t.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)
                : failed + passed + skipped;
            return new(failed, passed, skipped, total);
        }

        // mocha: "  5 passing (12ms)", "  1 failing", "  2 pending"
        var passing = Last(raw, @"^\s*(\d+) passing\b");
        var failing = Last(raw, @"^\s*(\d+) failing\b");
        if (passing.Success || failing.Success)
        {
            var pending = Num(Last(raw, @"^\s*(\d+) pending\b"));
            return new(Num(failing), Num(passing), pending, Num(failing) + Num(passing) + pending);
        }

        // node --test: "# pass 3", "# fail 0", "# skipped 0", "# tests 3"
        var pass = Last(raw, @"^# pass (\d+)");
        var fail = Last(raw, @"^# fail (\d+)");
        if (pass.Success || fail.Success)
        {
            var skip = Num(Last(raw, @"^# skipped (\d+)"));
            var all  = Last(raw, @"^# tests (\d+)");
            return new(Num(fail), Num(pass), skip, all.Success ? Num(all) : Num(fail) + Num(pass) + skip);
        }
        return null;
    }

    private static async Task<string> RunCargoAsync(string workDir, string? filter, RunBudget budget, CancellationToken ct)
    {
        // cargo searches up for Cargo.toml, but run from the crate/workspace root for predictability.
        var root = FindUp(workDir, "Cargo.toml") ?? workDir;
        var args = "test --quiet";
        if (!string.IsNullOrWhiteSpace(filter))
            args += $" {filter}";

        var (output, exitCode) = await RunProcessAsync("cargo", args, root, budget, ct);
        return ParseCargoOutput(output, exitCode);
    }

    private static async Task<string> RunGoAsync(string workDir, string? filter, RunBudget budget, CancellationToken ct)
    {
        var root = FindUp(workDir, "go.mod") ?? workDir;
        var args = "test ./...";
        if (!string.IsNullOrWhiteSpace(filter))
            args += $" -run \"{filter}\"";

        var (output, exitCode) = await RunProcessAsync("go", args, root, budget, ct);
        return ParseGoOutput(output, exitCode);
    }

    // ── Output parsers ─────────────────────────────────────────────────────────

    /// <summary>
    /// What a runner that exits 0 without a readable summary is worth: <b>nothing proven</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately carries no <c>✓</c>. <see cref="Commands.TddCommandHandler.TestsPassed"/> reads
    /// a leading <c>✓</c> as green, so this line keeps the loop running instead of letting it
    /// announce a victory nobody measured — same tactic as the "no test matched the filter"
    /// message, and same rule: <b>never infer green from an exit code alone</b>.
    /// </para>
    /// <para>
    /// A runner whose summary format moves — which is exactly what vstest did between SDK 8 and 9 —
    /// would otherwise turn every red run green, in silence. The raw output still follows, which is
    /// what the model actually reads.
    /// </para>
    /// </remarks>
    internal const string NothingProven =
        "⚠ The runner exited 0 but no test summary could be parsed — nothing was proven. " +
        "Read the raw output below; do not treat this as a pass.";

    /// <summary>The filter matched no test — the other way a run proves nothing.</summary>
    /// <remarks>
    /// A constant because <c>TddCommandHandler.NothingRan</c> has to recognise it: a sentence
    /// re-typed at the reading end keeps matching right up to the day the writing end is reworded,
    /// and then silently stops.
    /// </remarks>
    internal const string NoTestMatchedFilter =
        "⚠ No test matched the filter — nothing ran. " +
        "The tests may have been renamed or removed; that is not a pass.";

    /// <summary>The run was killed at its budget — the fourth state, and the only one that can
    /// carry a <b>green</b> summary while being worthless.</summary>
    /// <remarks>
    /// <para>
    /// The parsers read a log, not the clock: on a solution whose first project finishes and whose
    /// second hangs, the killed run still carries <c>Passed! - Failed: 0, Passed: 16</c>, and that
    /// became the whole report — <c>✓ PASSED</c>, with the projects that never ran nowhere in it.
    /// The rule this file already states about exit codes ("never infer green from an exit code
    /// alone") holds exactly as much for a summary that describes a fraction of the run.
    /// </para>
    /// <para>
    /// A constant for the same reason as its two neighbours: <c>TddCommandHandler</c> has to
    /// recognise this state, and a sentence re-typed at the reading end keeps matching right up to
    /// the day the writing end is reworded.
    /// </para>
    /// </remarks>
    internal const string StoppedAtBudget =
        "⚠ The test run was stopped before it finished — nothing was proven.";

    /// <summary>The sentence the report opens with when the budget ran out.</summary>
    internal static string StoppedAtBudgetLine(int seconds) =>
        $"{StoppedAtBudget} It exceeded its {seconds}s budget and the runner was killed, tree " +
        "included; anything below describes only the part that ran. Raise 'timeout_seconds', or " +
        "narrow 'filter' to the tests you are working on.";

    internal static string ParseDotnetOutput(string raw, int exitCode)
    {
        var sb = new StringBuilder();

        // Aggregate summary across all test projects
        // Format: "Passed! - Failed:     0, Passed:     5, Skipped:     0, Total:     5"
        var summaryRx = new Regex(
            @"(?:Passed|Failed)!\s*-\s*Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)",
            RegexOptions.Multiline | RegexOptions.Compiled, RegexBudget.Default);

        int totalFailed = 0, totalPassed = 0, totalSkipped = 0, totalTotal = 0;
        foreach (Match m in summaryRx.Matches(raw))
        {
            totalFailed  += int.Parse(m.Groups[1].Value);
            totalPassed  += int.Parse(m.Groups[2].Value);
            totalSkipped += int.Parse(m.Groups[3].Value);
            totalTotal   += int.Parse(m.Groups[4].Value);
        }

        // Modern vstest (SDK 9/10) prints a multi-line block instead — the single-line format above
        // never matches there, so every run fell through to "no summary line detected" (green) or
        // the raw dump (red):
        //   Test Run Successful.
        //   Total tests: 16
        //        Passed: 16
        //        Failed: 2        (only when non-zero)
        if (totalTotal == 0)
        {
            var blockRx = new Regex(
                @"^Total tests:\s*(\d+)\s*$(?:\r?\n^\s+Passed:\s*(\d+)\s*$)?(?:\r?\n^\s+Failed:\s*(\d+)\s*$)?(?:\r?\n^\s+Skipped:\s*(\d+)\s*$)?",
                RegexOptions.Multiline | RegexOptions.Compiled, RegexBudget.Default);
            foreach (Match m in blockRx.Matches(raw))
            {
                totalTotal   += int.Parse(m.Groups[1].Value);
                totalPassed  += m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
                totalFailed  += m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
                totalSkipped += m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0;
            }
        }

        if (totalTotal > 0)
        {
            var status = totalFailed == 0 ? "✓ PASSED" : "✗ FAILED";
            sb.AppendLine($"{status} — Failed: {totalFailed}, Passed: {totalPassed}, Skipped: {totalSkipped}, Total: {totalTotal}");
        }
        // A filter matching zero tests exits 0: read as a pass, an agent that renamed or deleted
        // the failing test makes `/tdd` declare victory on a run where nothing ran. The vstest
        // message is reliably English here because this tool forces the child's UI language.
        // No ✓/✗ prefix on purpose: callers' verdict parsing reads it as not-green and the loop
        // keeps working.
        else if (raw.Contains("No test matches the given testcase filter", StringComparison.Ordinal))
        {
            sb.AppendLine(NoTestMatchedFilter);
        }
        else if (exitCode == 0)
        {
            sb.AppendLine(NothingProven);
        }

        // Extract failed test blocks (name + error message, skip stack traces)
        var failures = ExtractDotnetFailures(raw);
        if (failures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Failing tests:");
            foreach (var (name, details) in failures)
            {
                sb.AppendLine($"  ✗ {name}");
                foreach (var d in details)
                    sb.AppendLine($"    {d}");
                sb.AppendLine();
            }
        }

        var result = sb.ToString().Trim();

        // Fallback: nothing could be parsed, return raw truncated
        if (string.IsNullOrEmpty(result))
            return Truncate(raw.Trim(), MaxRawChars);

        return result;
    }

    private static List<(string Name, List<string> Details)> ExtractDotnetFailures(string raw)
    {
        var failures = new List<(string, List<string>)>();
        var lines    = raw.Split('\n');

        string? currentName = null;
        var currentDetails  = new List<string>();
        bool inError        = false;
        bool inStack        = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r').Trim();

            // Start of a failed test: "Failed SomeName [10 ms]"
            if (Regex.IsMatch(line, @"^Failed\s+\S", RegexOptions.None, RegexBudget.Default))
            {
                if (currentName is not null)
                    failures.Add((currentName, currentDetails));

                var name = line["Failed ".Length..].Trim();
                // Remove trailing duration "[10 ms]"
                var bracket = name.LastIndexOf('[');
                if (bracket > 0) name = name[..bracket].Trim();

                currentName    = name;
                currentDetails = [];
                inError        = false;
                inStack        = false;
                continue;
            }

            if (currentName is null) continue;

            if (line == "Error Message:") { inError = true; inStack = false; continue; }
            if (line == "Stack Trace:")   { inStack = true; inError = false; continue; }

            if (inStack) continue; // skip stack frames entirely

            if (inError && !string.IsNullOrEmpty(line))
                currentDetails.Add(line);

            // Blank line after error details ends the block
            if (string.IsNullOrEmpty(line) && currentDetails.Count > 0)
            {
                failures.Add((currentName, currentDetails));
                currentName    = null;
                currentDetails = [];
                inError        = false;
            }
        }

        if (currentName is not null && currentDetails.Count > 0)
            failures.Add((currentName, currentDetails));

        return failures;
    }

    /// <summary>Failing test names listed at most — beyond it the list says how many it left out.</summary>
    /// <remarks>
    /// The list is a sample; the COUNT above it never is. A truncated list read as a complete one
    /// is how the `/tdd` loop concludes it has seen every failure — the same reasoning error the
    /// build-error path already paid for (SmartFixValidator).
    /// </remarks>
    private const int MaxFailingListed = 30;

    /// <summary>Says what the "Failing tests:" list left out, or nothing when it left out nothing.</summary>
    private static void AppendMoreFailures(StringBuilder sb, int total, int listed)
    {
        if (total > listed)
            sb.AppendLine($"  … +{total - listed} more failing test(s) not listed");
    }

    internal static string ParsePytestOutput(string raw, int exitCode)
    {
        var sb = new StringBuilder();

        // Summary: "= 1 failed, 5 passed in 1.23s ="
        var summaryMatch = Regex.Match(raw, @"=+\s*(.+?in\s+[\d.]+\s*s)\s*=+", RegexOptions.Multiline, RegexBudget.Default);
        if (summaryMatch.Success)
            sb.AppendLine(summaryMatch.Groups[1].Value.Trim());
        else if (exitCode == 0)
            sb.AppendLine(NothingProven);

        // FAILED lines: "FAILED tests/test_x.py::test_name - AssertionError: ..."
        var allFailedLines = raw.Split('\n')
            .Where(l => l.TrimStart().StartsWith("FAILED ", StringComparison.Ordinal))
            .Select(l => l.Trim())
            .ToList();
        var failedLines = allFailedLines.Take(MaxFailingListed).ToList();

        if (failedLines.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Failing tests:");
            foreach (var l in failedLines)
                sb.AppendLine("  " + l);
            AppendMoreFailures(sb, allFailedLines.Count, failedLines.Count);
        }

        var result = sb.ToString().Trim();
        return string.IsNullOrEmpty(result) ? Truncate(raw.Trim(), MaxRawChars) : result;
    }

    // Cargo prints one summary line per test binary:
    //   "test result: ok. 5 passed; 0 failed; 1 ignored; 0 measured; 0 filtered out"
    // and "test <name> ... FAILED" for each failure. Aggregates across binaries.
    internal static string ParseCargoOutput(string raw, int exitCode)
    {
        var sb = new StringBuilder();

        var summaryRx = new Regex(
            @"test result:\s*(?:ok|FAILED)\.\s*(\d+) passed;\s*(\d+) failed;\s*(\d+) ignored",
            RegexOptions.Multiline | RegexOptions.Compiled, RegexBudget.Default);

        int passed = 0, failed = 0, ignored = 0;
        bool any = false;
        foreach (Match m in summaryRx.Matches(raw))
        {
            any      = true;
            passed  += int.Parse(m.Groups[1].Value);
            failed  += int.Parse(m.Groups[2].Value);
            ignored += int.Parse(m.Groups[3].Value);
        }

        if (any)
            sb.AppendLine($"{(failed == 0 ? "✓ PASSED" : "✗ FAILED")} — Failed: {failed}, Passed: {passed}, Ignored: {ignored}, Total: {passed + failed + ignored}");
        else if (exitCode == 0)
            sb.AppendLine(NothingProven);

        var allFailing = raw.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("test ", StringComparison.Ordinal) && l.EndsWith("... FAILED", StringComparison.Ordinal))
            .Select(l => l["test ".Length..^"... FAILED".Length].Trim())
            .Distinct()
            .ToList();
        var failing = allFailing.Take(MaxFailingListed).ToList();

        if (failing.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Failing tests:");
            foreach (var name in failing)
                sb.AppendLine($"  ✗ {name}");
            AppendMoreFailures(sb, allFailing.Count, failing.Count);
        }

        var result = sb.ToString().Trim();
        return string.IsNullOrEmpty(result) ? Truncate(raw.Trim(), MaxRawChars) : result;
    }

    // Go prints "--- FAIL: TestName (0.00s)" per failing test (even without -v) and per-package
    // "ok|FAIL  import/path  0.0s" lines. The exit code is the overall pass/fail signal.
    internal static string ParseGoOutput(string raw, int exitCode)
    {
        var sb = new StringBuilder();

        // ⚠ Count BEFORE capping. Read off the capped list, a suite with eighty failures reports
        // "30 failing test(s)": the loop fixes thirty, re-runs, finds fifty, and reads them as
        // regressions it just introduced. go is the one runner with no summary of its own, so this
        // number is the only one the model gets.
        var allFailing = Regex.Matches(raw, @"^\s*--- FAIL:\s+(\S+)", RegexOptions.Multiline, RegexBudget.Default)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
        var failing = allFailing.Take(MaxFailingListed).ToList();

        if (exitCode == 0)
        {
            // go test prints no summary line: its exit code IS the verdict, and that is the one
            // runner where reading it is legitimate. What is not legitimate is reading it when
            // nothing ran: exit 0 without a single "ok <package>" line means every package was
            // "[no test files]" or matched nothing, and calling that a pass is the same silent
            // green as the three parsers above.
            var ranAPackage = Regex.IsMatch(raw, @"^ok\s+\S", RegexOptions.Multiline, RegexBudget.Default);
            sb.AppendLine(ranAPackage
                ? "✓ Tests passed (verdict from go's exit code — go test prints no summary)."
                : "⚠ go test exited 0 but no package reported \"ok\" — no test ran, so nothing " +
                  "was proven. Read the raw output below; do not treat this as a pass.");
        }
        else
            sb.AppendLine($"✗ FAILED — {(allFailing.Count > 0 ? $"{allFailing.Count} failing test(s)" : "see output")}");

        if (failing.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Failing tests:");
            foreach (var name in failing)
                sb.AppendLine($"  ✗ {name}");
            AppendMoreFailures(sb, allFailing.Count, failing.Count);
        }

        // Surface the diagnostic lines Go prints under each failure (file:line: message).
        var detail = raw.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => Regex.IsMatch(l, @"\.go:\d+:", RegexOptions.None, RegexBudget.Default))
            .Select(l => l.Trim())
            .Distinct()
            .Take(20)
            .ToList();
        if (exitCode != 0 && detail.Count > 0)
        {
            sb.AppendLine();
            foreach (var l in detail)
                sb.AppendLine("  " + l);
        }

        var result = sb.ToString().Trim();
        return string.IsNullOrEmpty(result) ? Truncate(raw.Trim(), MaxRawChars) : result;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The budget of one run, and whether it ran out.
    /// </summary>
    /// <remarks>
    /// ⚠ It travels DOWN to <see cref="RunProcessAsync"/> so the fact can be said ONCE, at the
    /// funnel in <see cref="ExecuteAsync"/>. Said at the five runner sites instead, the sixth
    /// runner would be written without it — and the failure is silent, because the parsers happily
    /// compose a verdict out of a partial log.
    /// </remarks>
    private sealed class RunBudget(int seconds)
    {
        public int  Seconds { get; }      = seconds;
        public bool Expired { get; set; }

        public string Wrap(string report) =>
            Expired ? StoppedAtBudgetLine(Seconds) + "\n\n" + report : report;
    }

    private static async Task<(string Output, int ExitCode)> RunProcessAsync(
        string fileName, string arguments, string workDir, RunBudget budget, CancellationToken ct,
        IReadOnlyList<string>? argumentList = null)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = fileName,
                Arguments              = arguments,
                WorkingDirectory       = workDir,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
            };
            // A list, when given, is passed as such: each element one argument, no quoting to get wrong.
            foreach (var a in argumentList ?? []) psi.ArgumentList.Add(a);

            // The dotnet/vstest summary lines this tool parses ("Passed! - Failed: …",
            // "Failed X [10 ms]", "Error Message:") are localized by the SDK: on a French machine
            // every red run fell through to the raw-log fallback — a truncated MSBuild wall instead
            // of test names and assert messages. Forcing the child's UI language keeps the parser
            // input deterministic on every locale.
            psi.EnvironmentVariables["DOTNET_CLI_UI_LANGUAGE"] = "en";
            psi.EnvironmentVariables["VSLANG"]                 = "1033";

            var run = await ChildProcess.RunAsync(psi, TimeSpan.FromSeconds(budget.Seconds), ct);

            // A test run that overruns its budget keeps the partial log rather than throwing a
            // cancellation, which would abort the agent turn and lose every line already produced.
            // The runner is killed, tree included, or a stray `dotnet test` survives it.
            // ⚠ The FACT travels in the budget, not in this text: written into the log, the
            // sentence reached a parser that does not read prose, and a summary from the projects
            // that had finished became the verdict of a run that was killed.
            if (run.TimedOut)
            {
                budget.Expired = true;
                return (run.Combined, -1);
            }

            return (run.Combined, run.ExitCode);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)               { return ($"Failed to start '{fileName}': {ex.Message}", -1); }
    }

    /// <summary>
    /// "Nothing here to run" — plus, when it is true, "and there is a folder I could not look in".
    /// </summary>
    /// <remarks>
    /// ⚠ <see cref="DetectRunner"/> decides by WALKING, and a folder the walk cannot list leaves no
    /// trace in any count: the answer is self-consistent and reads as a fact about the repository
    /// when it is a fact about what this process may open. The remedy on its own makes it worse —
    /// "provide 'path'" points at a file inside the folder nobody can open. The gap is added as a
    /// CAUSE, never as a replacement: the remedy is still right in every other case.
    /// ⚠ The detector is only asked on this branch, so a workspace that resolves its runner pays
    /// nothing for it.
    /// </remarks>
    private static string NoRunnerDetected(string workDir, string? root)
    {
        const string message = "No test runner detected. Provide 'path' to a project, or set "
                             + "'runner' explicitly (dotnet / pytest / npm / cargo / go).";

        // The sentence belongs to WalkGap: "cannot be listed" and "is a link, not followed" send
        // the reader to two different places, and that choice is made in one place only.
        return WorkspaceScan.FirstWalkGap(workDir, root) is { } gap
            ? $"{message}\n({gap.Sentence()})"
            : message;
    }

    private static string DetectRunner(string workDir, string? explicitPath)
    {
        if (explicitPath is not null)
        {
            // SolutionFiles recognises BOTH formats: a `path` pointing at a .slnx otherwise
            // picked the wrong runner.
            if (SolutionFiles.IsSolution(explicitPath) ||
                explicitPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                return "dotnet";
        }

        // WorkspaceScan: lazy (GetFiles materialized the whole tree before .Any()) and excluded
        // dirs skipped — a vendored .sln under node_modules must not flip the runner to dotnet.
        if (WorkspaceScan.EnumerateFiles(workDir, "*.sln").Any()  ||
            WorkspaceScan.EnumerateFiles(workDir, "*.slnx").Any() ||
            WorkspaceScan.EnumerateFiles(workDir, "*.csproj").Any())
            return "dotnet";

        if (File.Exists(Path.Combine(workDir, "pytest.ini"))     ||
            File.Exists(Path.Combine(workDir, "pyproject.toml")) ||
            File.Exists(Path.Combine(workDir, "conftest.py"))    ||
            WorkspaceScan.EnumerateFiles(workDir, "conftest.py").Any())
            return "pytest";

        if (File.Exists(Path.Combine(workDir, "package.json")))
            return "npm";

        if (FindUp(workDir, "Cargo.toml") is not null)
            return "cargo";

        if (FindUp(workDir, "go.mod") is not null)
            return "go";

        return "unknown";
    }

    // Walks up from <paramref name="startDir"/> looking for <paramref name="fileName"/>; returns the
    // directory containing it, or <c>null</c>. Used to locate a Rust crate / Go module root from a
    // nested working directory.
    private static string? FindUp(string startDir, string fileName)
    {
        var dir = startDir;
        for (int i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
        {
            try { if (File.Exists(Path.Combine(dir, fileName))) return dir; }
            catch { return null; }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>Where the runner starts: the path given, else the workspace root — the working
    /// directory only when no workspace root is known.</summary>
    internal static string ResolveWorkDir(string? path, string? root)
    {
        var fallback = string.IsNullOrEmpty(root) ? Directory.GetCurrentDirectory() : root;
        if (path is null)                    return fallback;
        if (Directory.Exists(path))          return path;
        if (File.Exists(path))               return Path.GetDirectoryName(path) ?? fallback;
        return fallback;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + $"\n...[truncated — {s.Length - max} more characters]";
}
