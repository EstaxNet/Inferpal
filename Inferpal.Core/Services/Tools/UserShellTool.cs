using System.Diagnostics;
using System.Text.Json;
using Inferpal.Config;

namespace Inferpal.Services.Tools;

/// <summary>User-defined tool that runs a configurable shell command.</summary>
internal sealed class UserShellTool(string name, string command, IApprovalService approval, InferpalConfig config) : ITool
{
    public string Name        => name;
    public string Description => $"User-defined tool. Runs: {command}";
    public object Parameters  => new
    {
        type       = "object",
        properties = new
        {
            args = new { type = "string", description = "Optional extra arguments appended to the command." }
        },
        required = Array.Empty<string>(),
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var extra   = args.TryGetProperty("args", out var a) ? a.GetString() : null;
        var fullCmd = string.IsNullOrEmpty(extra) ? command : $"{command} {extra}";

        if (!await approval.RequestApprovalAsync(name, fullCmd, ct))
            return "Cancelled.";

        try
        {
            // Resolved per machine like run_command: powershell.exe was hard-coded here, so every
            // user-defined tool died on the published linux-x64/darwin-arm64 hosts with "cannot
            // start process 'powershell.exe'" (pre-1.6.0 architecture review). The encoding contract is
            // ShellLauncher's: -EncodedCommand on PowerShell, ArgumentList -c on POSIX — neither
            // goes through a shell quoting layer. `args` are still appended INTO the script by
            // design — the approval prompt above (full command shown) is the actual guard.
            var (dialect, shell) = Shell.ShellLauncher.Resolve();
            var psi = Shell.ShellLauncher.BuildStartInfo(dialect, shell, fullCmd);

            // Concurrent drain of both pipes and a killed process tree on timeout live in
            // ChildProcess now — this tool was the one site that had both right, and the shared
            // implementation is its behaviour.
            var run = await ChildProcess.RunAsync(
                psi, TimeSpan.FromSeconds(config.CommandTimeoutSeconds), ct);

            // Timeout is reported to the model, not thrown: it must not abort the whole agent run.
            if (run.TimedOut)
                return $"Error: command timed out after {config.CommandTimeoutSeconds}s.";

            var result = (run.Stdout + run.Stderr).Trim();
            return string.IsNullOrEmpty(result) ? "(no output)" : result;
        }
        catch (OperationCanceledException) { throw; } // user cancelled
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }
}
