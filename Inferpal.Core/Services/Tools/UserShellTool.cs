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
            // Base64 UTF-16 keeps the script intact across the process command line (quoting,
            // '&', newlines…). It does NOT sandbox `args`: they are appended INTO the script and
            // may contain arbitrary PowerShell by design — the approval prompt above (which shows
            // the full command, args included) and the permission rules are the actual guard.
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(fullCmd));

            var psi = new ProcessStartInfo
            {
                FileName  = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
            };

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
