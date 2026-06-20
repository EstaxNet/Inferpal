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
            // Encode the whole script as Base64 UTF-16 so LLM-supplied `args` can't break
            // out of the command via shell metacharacters (mirrors RunCommandTool).
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(fullCmd));

            var psi = new ProcessStartInfo
            {
                FileName               = "powershell.exe",
                Arguments              = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(config.CommandTimeoutSeconds));

            using var proc = Process.Start(psi)!;
            // Read stdout and stderr concurrently to prevent buffer-full deadlocks.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Timeout or user cancel: Process.Dispose() does NOT terminate the native
                // process, so kill the whole tree to avoid leaving an orphaned powershell.exe.
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            var output = await stdoutTask;
            var error  = await stderrTask;

            var result = (output + error).Trim();
            return string.IsNullOrEmpty(result) ? "(no output)" : result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } // user cancelled
        catch (OperationCanceledException)
        {
            // Internal timeout — report it to the model instead of aborting the whole agent run.
            return $"Error: command timed out after {config.CommandTimeoutSeconds}s.";
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }
}
