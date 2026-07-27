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
                description = "Path to a project file (.sln/.csproj), directory, or test file. Optional, defaults to cwd."
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
        var rawPath = args.TryGetProperty("path",           out var p) ? p.GetString()?.Trim() : null;
        var path    = string.IsNullOrWhiteSpace(rawPath) ? null : PathSanitizer.Sanitize(rawPath);
        var filter  = args.TryGetProperty("filter",         out var f) ? f.GetString()?.Trim() : null;
        var forced  = args.TryGetProperty("runner",         out var r) ? r.GetString()?.Trim().ToLowerInvariant() : null;
        var timeout = args.TryGetProperty("timeout_seconds",out var t) && t.TryGetInt32(out var ts) ? ts : DefaultTimeoutSeconds;

        var workDir = ResolveWorkDir(path);
        var runner  = (forced is null or "auto") ? DetectRunner(workDir, path) : forced;

        return runner switch
        {
            "dotnet" => await RunDotnetAsync(workDir, path, filter, timeout, ct),
            "pytest" => await RunPytestAsync(workDir, filter, timeout, ct),
            "npm"    => await RunNpmAsync(workDir, filter, timeout, ct),
            "cargo"  => await RunCargoAsync(workDir, filter, timeout, ct),
            "go"     => await RunGoAsync(workDir, filter, timeout, ct),
            _        => "No test runner detected. Provide 'path' to a project, or set 'runner' explicitly (dotnet / pytest / npm / cargo / go).",
        };
    }

    // ── Runner implementations ─────────────────────────────────────────────────

    private static async Task<string> RunDotnetAsync(string workDir, string? path, string? filter, int timeout, CancellationToken ct)
    {
        var sb = new StringBuilder("test");
        if (!string.IsNullOrWhiteSpace(path))
            sb.Append($" \"{path}\"");
        sb.Append(" --verbosity normal --nologo");
        if (!string.IsNullOrWhiteSpace(filter))
            sb.Append($" --filter \"{filter}\"");

        var (output, exitCode) = await RunProcessAsync("dotnet", sb.ToString(), workDir, timeout, ct);
        return ParseDotnetOutput(output, exitCode);
    }

    private static async Task<string> RunPytestAsync(string workDir, string? filter, int timeout, CancellationToken ct)
    {
        var args = "-m pytest -v --tb=short -q";
        if (!string.IsNullOrWhiteSpace(filter))
            args += $" -k \"{filter}\"";

        var (output, exitCode) = await RunProcessAsync("python", args, workDir, timeout, ct);
        return ParsePytestOutput(output, exitCode);
    }

    private static async Task<string> RunNpmAsync(string workDir, string? filter, int timeout, CancellationToken ct)
    {
        var args = "test";
        if (!string.IsNullOrWhiteSpace(filter))
            args += $" -- --testNamePattern=\"{filter}\"";

        var (output, _) = await RunProcessAsync("npm", args, workDir, timeout, ct);
        return Truncate(output.Trim(), MaxRawChars);
    }

    private static async Task<string> RunCargoAsync(string workDir, string? filter, int timeout, CancellationToken ct)
    {
        // cargo searches up for Cargo.toml, but run from the crate/workspace root for predictability.
        var root = FindUp(workDir, "Cargo.toml") ?? workDir;
        var args = "test --quiet";
        if (!string.IsNullOrWhiteSpace(filter))
            args += $" {filter}";

        var (output, exitCode) = await RunProcessAsync("cargo", args, root, timeout, ct);
        return ParseCargoOutput(output, exitCode);
    }

    private static async Task<string> RunGoAsync(string workDir, string? filter, int timeout, CancellationToken ct)
    {
        var root = FindUp(workDir, "go.mod") ?? workDir;
        var args = "test ./...";
        if (!string.IsNullOrWhiteSpace(filter))
            args += $" -run \"{filter}\"";

        var (output, exitCode) = await RunProcessAsync("go", args, root, timeout, ct);
        return ParseGoOutput(output, exitCode);
    }

    // ── Output parsers ─────────────────────────────────────────────────────────

    private static string ParseDotnetOutput(string raw, int exitCode)
    {
        var sb = new StringBuilder();

        // Aggregate summary across all test projects
        // Format: "Passed! - Failed:     0, Passed:     5, Skipped:     0, Total:     5"
        var summaryRx = new Regex(
            @"(?:Passed|Failed)!\s*-\s*Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)",
            RegexOptions.Multiline | RegexOptions.Compiled);

        int totalFailed = 0, totalPassed = 0, totalSkipped = 0, totalTotal = 0;
        foreach (Match m in summaryRx.Matches(raw))
        {
            totalFailed  += int.Parse(m.Groups[1].Value);
            totalPassed  += int.Parse(m.Groups[2].Value);
            totalSkipped += int.Parse(m.Groups[3].Value);
            totalTotal   += int.Parse(m.Groups[4].Value);
        }

        if (totalTotal > 0)
        {
            var status = totalFailed == 0 ? "✓ PASSED" : "✗ FAILED";
            sb.AppendLine($"{status} — Failed: {totalFailed}, Passed: {totalPassed}, Skipped: {totalSkipped}, Total: {totalTotal}");
        }
        else if (exitCode == 0)
        {
            sb.AppendLine("✓ Tests passed (no summary line detected).");
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
            if (Regex.IsMatch(line, @"^Failed\s+\S"))
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

    private static string ParsePytestOutput(string raw, int exitCode)
    {
        var sb = new StringBuilder();

        // Summary: "= 1 failed, 5 passed in 1.23s ="
        var summaryMatch = Regex.Match(raw, @"=+\s*(.+?in\s+[\d.]+\s*s)\s*=+", RegexOptions.Multiline);
        if (summaryMatch.Success)
            sb.AppendLine(summaryMatch.Groups[1].Value.Trim());
        else if (exitCode == 0)
            sb.AppendLine("✓ All tests passed.");

        // FAILED lines: "FAILED tests/test_x.py::test_name - AssertionError: ..."
        var failedLines = raw.Split('\n')
            .Where(l => l.TrimStart().StartsWith("FAILED ", StringComparison.Ordinal))
            .Select(l => l.Trim())
            .Take(30)
            .ToList();

        if (failedLines.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Failing tests:");
            foreach (var l in failedLines)
                sb.AppendLine("  " + l);
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
            RegexOptions.Multiline | RegexOptions.Compiled);

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
            sb.AppendLine("✓ Tests passed (no summary line detected).");

        var failing = raw.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("test ", StringComparison.Ordinal) && l.EndsWith("... FAILED", StringComparison.Ordinal))
            .Select(l => l["test ".Length..^"... FAILED".Length].Trim())
            .Distinct()
            .Take(30)
            .ToList();

        if (failing.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Failing tests:");
            foreach (var name in failing)
                sb.AppendLine($"  ✗ {name}");
        }

        var result = sb.ToString().Trim();
        return string.IsNullOrEmpty(result) ? Truncate(raw.Trim(), MaxRawChars) : result;
    }

    // Go prints "--- FAIL: TestName (0.00s)" per failing test (even without -v) and per-package
    // "ok|FAIL  import/path  0.0s" lines. The exit code is the overall pass/fail signal.
    internal static string ParseGoOutput(string raw, int exitCode)
    {
        var sb = new StringBuilder();

        var failing = Regex.Matches(raw, @"^\s*--- FAIL:\s+(\S+)", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .Take(30)
            .ToList();

        if (exitCode == 0)
            sb.AppendLine("✓ Tests passed.");
        else
            sb.AppendLine($"✗ FAILED — {(failing.Count > 0 ? $"{failing.Count} failing test(s)" : "see output")}");

        if (failing.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Failing tests:");
            foreach (var name in failing)
                sb.AppendLine($"  ✗ {name}");
        }

        // Surface the diagnostic lines Go prints under each failure (file:line: message).
        var detail = raw.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => Regex.IsMatch(l, @"\.go:\d+:"))
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

    private static async Task<(string Output, int ExitCode)> RunProcessAsync(
        string fileName, string arguments, string workDir, int timeoutSeconds, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = fileName,
                Arguments              = arguments,
                WorkingDirectory       = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            using var proc = Process.Start(psi)!;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);

            var output = await stdoutTask + "\n" + await stderrTask;
            return (output, proc.ExitCode);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)               { return ($"Failed to start '{fileName}': {ex.Message}", -1); }
    }

    private static string DetectRunner(string workDir, string? explicitPath)
    {
        if (explicitPath is not null)
        {
            if (explicitPath.EndsWith(".sln",   StringComparison.OrdinalIgnoreCase) ||
                explicitPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                return "dotnet";
        }

        if (Directory.GetFiles(workDir, "*.sln",   SearchOption.AllDirectories).Any() ||
            Directory.GetFiles(workDir, "*.csproj", SearchOption.AllDirectories).Any())
            return "dotnet";

        if (File.Exists(Path.Combine(workDir, "pytest.ini"))     ||
            File.Exists(Path.Combine(workDir, "pyproject.toml")) ||
            File.Exists(Path.Combine(workDir, "conftest.py"))    ||
            Directory.GetFiles(workDir, "conftest.py", SearchOption.AllDirectories).Any())
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

    private static string ResolveWorkDir(string? path)
    {
        if (path is null)                    return Directory.GetCurrentDirectory();
        if (Directory.Exists(path))          return path;
        if (File.Exists(path))               return Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
        return Directory.GetCurrentDirectory();
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + $"\n...[truncated — {s.Length - max} more characters]";
}
