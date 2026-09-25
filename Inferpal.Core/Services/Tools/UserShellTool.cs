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
        var extra   = args.Str("args");
        var fullCmd = string.IsNullOrEmpty(extra) ? command : $"{command} {extra}";

        if (!await approval.RequestApprovalAsync(name, fullCmd, ct))
            return "Cancelled.";

        try
        {
            // Resolved per machine like run_command: powershell.exe was hard-coded here, so every
            // user-defined tool died on the published linux-x64/darwin-arm64 hosts with "cannot
            // start process 'powershell.exe'". The encoding contract is
            // ShellLauncher's: -EncodedCommand on PowerShell, ArgumentList -c on POSIX — neither
            // goes through a shell quoting layer. `args` are still appended INTO the script by
            // design — the approval prompt above (full command shown) is the actual guard.
            var (dialect, shell) = Shell.ShellLauncher.Resolve();
            var psi = Shell.ShellLauncher.BuildStartInfo(dialect, shell, fullCmd);

            // Concurrent drain of both pipes and a killed process tree on timeout live in
            // ChildProcess, shared with every other child this product starts.
            var run = await ChildProcess.RunAsync(
                psi, TimeSpan.FromSeconds(config.CommandTimeoutSeconds), ct);
            run = run with { Stderr = Shell.PowerShellStderr.Decode(run.Stderr) };   // CLIXML → text

            // Timeout is reported to the model, not thrown: it must not abort the whole agent run —
            // and it carries what the command had printed, like the persistent shell. ChildProcess
            // hands the partial streams back; dropping them tells the model only the time.
            if (run.TimedOut)
                return ChildProcess.TimedOutMessage(config.CommandTimeoutSeconds,
                                                    (run.Stdout + run.Stderr).Trim());

            var result = (run.Stdout + run.Stderr).Trim();
            return string.IsNullOrEmpty(result) ? "(no output)" : result;
        }
        catch (OperationCanceledException) { throw; } // user cancelled
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }
}
