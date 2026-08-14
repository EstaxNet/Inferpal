using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;

namespace Inferpal.Services.CodeActions;

/// <summary>
/// Runs a quick build / typecheck after a file write and returns any compilation <em>errors</em>
/// (warnings are ignored) as a formatted string, or <c>null</c> when disabled, the file type has no
/// validator, no project root is found, the toolchain is absent, or the build is clean.
/// </summary>
/// <remarks>
/// Polyglot: the ecosystem is chosen by <see cref="BuildValidators"/> from the edited file's
/// extension (built-in .NET / TypeScript / Rust / Go defaults, plus the workspace
/// <c>.inferpal/validators.json</c> overlay). The .NET path is unchanged (parses <c>": error XX:"</c>
/// lines); other ecosystems use the process exit code. Never throws — a failed check returns
/// <c>null</c> so the write tool is never broken.
/// </remarks>
internal sealed class SmartFixValidator
{
    private readonly InferpalConfig    _config;
    private readonly Func<string?>     _getWorkspaceRoot;
    private readonly IApprovalService? _approval;

    /// <summary>Workspace commands approved during this session — asked once, not once per file.</summary>
    private readonly HashSet<string> _approvedCommands = new(StringComparer.Ordinal);

    /// <param name="approval">
    /// Required to run a command coming from <c>.inferpal/validators.json</c>. When absent, such a
    /// command is <b>refused</b> rather than run unattended: no approval surface, no execution.
    /// </param>
    public SmartFixValidator(
        InferpalConfig config, Func<string?>? getWorkspaceRoot = null, IApprovalService? approval = null)
    {
        _config           = config;
        _getWorkspaceRoot = getWorkspaceRoot ?? (() => null);
        _approval         = approval;
    }

    /// <summary>Asks once per session for a repository-authored build command.</summary>
    private async Task<bool> ApproveWorkspaceCommandAsync(string command, CancellationToken ct)
    {
        if (_approvedCommands.Contains(command)) return true;

        if (_approval is null)
        {
            Diagnostics.Record("Permission",
                $"Refused workspace validator (no approval surface): {command}");
            return false;
        }

        bool approved;
        try
        {
            approved = await _approval.RequestApprovalAsync(
                "smart_fix_validator", command, ct, subject: command, diff: null, forcePrompt: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Never let the approval path break a file write — but never fall through to running
            // the command either.
            Diagnostics.Swallow("SmartFixValidator.Approval", ex);
            return false;
        }

        if (approved) _approvedCommands.Add(command);
        else Diagnostics.Record("Permission", $"Declined workspace validator: {command}");
        return approved;
    }

    // Output patterns that mean the toolchain itself is missing (npx/cargo/go/tsc not on PATH), as
    // opposed to genuine compilation errors. Reported silently (null) so an unconfigured machine
    // doesn't get spammed with "errors" that are really "tool not installed".
    private static readonly Regex ToolMissingRegex = new(
        @"is not recognized as|n'est pas reconnu|command not found|No such file|cannot find the path|could not be found|ENOENT",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<string?> ValidateAsync(string writtenFilePath, CancellationToken ct)
    {
        if (!_config.SmartFixEnabled) return null;

        var validators = BuildValidators.Resolve(LoadOverlay());
        var match = BuildValidators.Match(writtenFilePath, validators, FindMarker);
        if (match is null) return null;

        var (validator, projectDir, projectFile) = match.Value;
        var command = validator.Command.Replace("{project}", projectFile);

        // Safety: never auto-run a catastrophic command sourced from a (possibly committed)
        // validators.json. Shares the built-in hard denylist with the approval policy (axe 1).
        if (PermissionPolicy.IsHardDenied(command)) return null;

        // ⚠ A validator from `.inferpal/validators.json` was written by the REPOSITORY, and Smart
        // Fix runs it by itself after a write — so cloning a repository and letting the agent touch
        // one file would execute that repository's command, silently. The denylist is no defence
        // here: it matches text, and obfuscation walks around it by construction. The frontier is
        // the approval prompt, where the human reads the raw command, so that is where this goes —
        // force-prompted, because no consent the user gave their own agent covers a stranger's
        // command. Built-in validators (dotnet, tsc, cargo, go) are ours and keep running silently.
        if (validator.FromWorkspace && !await ApproveWorkspaceCommandAsync(command, ct))
            return null;

        try
        {
            var (exitCode, output) = await RunAsync(command, projectDir, ct);

            if (validator.UseDotnetErrorFilter)
            {
                // .NET: unchanged behaviour — errors only (warnings don't warrant a fix iteration).
                if (!GetDiagnosticsTool.OutputHasBuildErrors(output))
                    return Strings.SmartFixBuildOk;

                var errors = output
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Where(l => GetDiagnosticsTool.ErrorLineRegex.IsMatch(l))
                    .Select(l => l.Trim())
                    .Distinct()
                    .Take(20)
                    .ToList();
                return Strings.SmartFixBuildErrors(errors.Count, string.Join("\n", errors));
            }

            // Generic ecosystems: the exit code is the reliable failure signal across toolchains.
            if (exitCode == 0) return Strings.SmartFixBuildOk;
            if (ToolMissingRegex.IsMatch(output)) return null;   // toolchain absent → stay silent

            var lines = ExtractErrorLines(output);
            return Strings.SmartFixBuildErrors(lines.Count, string.Join("\n", lines));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // user cancelled the agent run
        }
        catch (OperationCanceledException)
        {
            return Strings.SmartFixTimeout;
        }
        catch
        {
            // Never crash the write tool — a failed build check is best-effort.
            return null;
        }
    }

    // Prefer lines that look like compiler errors ("error" anywhere); fall back to all non-empty
    // lines (e.g. Go's `file.go:10:5: msg` format has no "error" keyword). Capped for the context.
    private static List<string> ExtractErrorLines(string output)
    {
        var all = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        var errorish = all.Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).ToList();
        return (errorish.Count > 0 ? errorish : all).Take(25).ToList();
    }

    private string? FindMarker(string dir, string glob)
    {
        try { return Directory.GetFiles(dir, glob, SearchOption.TopDirectoryOnly).FirstOrDefault(); }
        catch { return null; }
    }

    private IReadOnlyList<BuildValidator> LoadOverlay()
    {
        var root = _getWorkspaceRoot();
        if (string.IsNullOrEmpty(root)) return [];
        var path = Path.Combine(root, ".inferpal", "validators.json");
        try
        {
            return File.Exists(path) ? BuildValidators.ParseConfig(File.ReadAllText(path)) : [];
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("ValidatorsOverlayRead", ex);
            return [];
        }
    }

    // Runs the command line in PowerShell (Base64-encoded to avoid metacharacter issues), in the
    // project directory, with a 60s fuse. Returns (exit code, combined stdout+stderr).
    private static async Task<(int ExitCode, string Output)> RunAsync(string command, string workDir, CancellationToken ct)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var psi = new ProcessStartInfo
        {
            FileName         = "powershell.exe",
            Arguments        = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
            WorkingDirectory = workDir,
        };

        var run = await ChildProcess.RunAsync(psi, TimeSpan.FromSeconds(60), ct);

        // A validator that hangs is a failed validation, not a cancelled edit: -1 with the partial
        // output lets the caller reject the write and show why, where the thrown cancellation used
        // to surface as if the user had stopped it.
        return (run.TimedOut ? -1 : run.ExitCode, run.Combined);
    }
}
