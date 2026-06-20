using System.Text;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Shell;

namespace Inferpal.Services.Tools;

/// <summary>
/// Runs PowerShell commands for the agent. The shell is a <em>persistent session</em>: working
/// directory and environment variables set by one command are preserved for the next (see
/// <see cref="ShellSession"/>). Long-running commands can be launched in the <em>background</em>
/// and then read incrementally (<c>action: "poll"</c>) or terminated (<c>action: "stop"</c>).
/// </summary>
internal sealed class RunCommandTool : ITool
{
    private readonly IApprovalService _approval;
    private readonly ShellSession _session;
    private readonly BackgroundShellRegistry _background = new();

    public RunCommandTool(IApprovalService approval, InferpalConfig config, Func<string> root)
    {
        _approval = approval;
        _session  = new ShellSession(root, config);
    }

    public string Name => "run_command";

    public string Description =>
        "Runs a PowerShell command in a persistent session (working directory and environment variables "
        + "set by 'cd' or '$env:' persist to later calls). Set background=true for long-running commands "
        + "(builds, servers, watchers): it returns a job id immediately. Use action='poll' with that id to "
        + "read new output, and action='stop' to terminate it.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            command           = new { type = "string", description = "PowerShell command to execute. Required unless using action=poll/stop/list." },
            working_directory = new { type = "string", description = "Working directory for this command (optional; otherwise the session's current directory)." },
            background        = new { type = "boolean", description = "Run detached and return a job id immediately instead of waiting (for long-running commands)." },
            action            = new { type = "string", description = "Manage a background job instead of running a command: 'poll' (read new output), 'stop' (terminate), or 'list'." },
            id                = new { type = "string", description = "The background job id (returned when background=true). Required for action='poll'/'stop'." }
        },
        required = Array.Empty<string>()
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var action = args.TryGetProperty("action", out var ac) ? ac.GetString() : null;
        if (!string.IsNullOrWhiteSpace(action))
            return HandleAction(action!.Trim().ToLowerInvariant(), args);

        if (!args.TryGetProperty("command", out var cmdEl) || cmdEl.GetString() is not { Length: > 0 } command)
            return "Error: 'command' is required (or use action='poll'/'stop'/'list').";

        var rawWorkDir = args.TryGetProperty("working_directory", out var wd) ? wd.GetString() : null;
        var workDir    = string.IsNullOrWhiteSpace(rawWorkDir) ? null : PathSanitizer.Sanitize(rawWorkDir);

        if (!await _approval.RequestApprovalAsync("run_command", command, ct))
            return Strings.RunCancelled;

        var background = args.TryGetProperty("background", out var bg) && bg.ValueKind == JsonValueKind.True;
        if (background)
        {
            var (cwd, env) = _session.Snapshot();
            var startCwd   = workDir ?? cwd;
            var script     = ShellStateProtocol.BuildBackgroundScript(startCwd, env, command);
            var id         = _background.Start(script, command, startCwd);
            return $"Started background job '{id}'. Use action='poll' id='{id}' to read its output, action='stop' id='{id}' to terminate it.";
        }

        return await _session.RunAsync(command, workDir, ct);
    }

    private string HandleAction(string action, JsonElement args)
    {
        switch (action)
        {
            case "poll":
            {
                var id = GetId(args);
                if (id is null) return "Error: action='poll' requires 'id'.";
                var r = _background.Poll(id);
                if (!r.Found) return $"Error: no background job '{id}' (it may have finished and been drained already).";
                var status = r.Running ? "still running" : $"finished (exit code {r.ExitCode?.ToString() ?? "unknown"})";
                var body   = string.IsNullOrEmpty(r.NewOutput) ? "(no new output)" : r.NewOutput.TrimEnd();
                return $"[job '{id}' — {status}]\n{body}";
            }
            case "stop":
            {
                var id = GetId(args);
                if (id is null) return "Error: action='stop' requires 'id'.";
                return _background.Stop(id)
                    ? $"Stopped background job '{id}'."
                    : $"Error: no background job '{id}'.";
            }
            case "list":
            {
                var jobs = _background.List();
                if (jobs.Count == 0) return "No background jobs.";
                var sb = new StringBuilder("Background jobs:\n");
                foreach (var (jid, cmd, running) in jobs)
                    sb.Append($"- {jid} [{(running ? "running" : "finished")}] {cmd}\n");
                return sb.ToString().TrimEnd();
            }
            default:
                return $"Error: unknown action '{action}' (expected 'poll', 'stop', or 'list').";
        }
    }

    private static string? GetId(JsonElement args) =>
        args.TryGetProperty("id", out var idEl) && idEl.GetString() is { Length: > 0 } id ? id : null;
}
