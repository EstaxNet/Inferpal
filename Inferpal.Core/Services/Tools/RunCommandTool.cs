using System.Text;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Shell;

namespace Inferpal.Services.Tools;

/// <summary>
/// Runs shell commands for the agent. The shell is a <em>persistent session</em>: working
/// directory and environment variables set by one command are preserved for the next (see
/// <see cref="ShellSession"/>). Long-running commands can be launched in the <em>background</em>
/// and then read incrementally (<c>action: "poll"</c>) or terminated (<c>action: "stop"</c>).
/// </summary>
internal sealed class RunCommandTool : ITool, IDisposable
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

    /// <summary>
    /// How this machine's shell is named to the model, and how it sets an environment variable.
    /// </summary>
    /// <remarks>
    /// ⚠ Read from <see cref="ShellLauncher"/>, never written down. The execution path has spoken
    /// two dialects since §23 (PowerShell on Windows or where <c>pwsh</c> is on PATH, POSIX
    /// otherwise), but this description — the only thing telling the model <i>how to write the
    /// command</i> — still said "PowerShell" everywhere. On the published Linux and macOS VSIX that
    /// is a Windows script handed to <c>/bin/bash</c>: the model writes <c>Get-ChildItem</c> and
    /// <c>$env:FOO</c>, bash refuses them, and the agent spends its iteration budget discovering by
    /// trial and error what the tool could have said in one sentence. <c>UserShellTool</c> carries
    /// the same fault on the execution side, and it was repaired there in the pre-1.6.0 review.
    /// </remarks>
    private static (string Shell, string SetEnv) Speak() =>
        ShellLauncher.Resolve().Dialect == ShellDialect.PowerShell
            ? ("PowerShell", "$env:NAME='…'")
            : ("bash",       "export NAME=…");

    public string Description
    {
        get
        {
            var (shell, setEnv) = Speak();
            return $"Runs a {shell} command in a persistent session (working directory and environment "
                 + $"variables set by 'cd' or `{setEnv}` persist to later calls). Set background=true for "
                 + "long-running commands (builds, servers, watchers): it returns a job id immediately. Use "
                 + "action='poll' with that id to read new output, and action='stop' to terminate it.";
        }
    }

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            command           = new { type = "string", description = $"{Speak().Shell} command to execute. Required unless using action=poll/stop/list." },
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

        // Surface a model-chosen working directory in the prompt: approving "git clean -fdx"
        // reads very differently when it runs outside the session cwd the user has in mind.
        // Permission rules keep matching the raw command (the documented subject for run_command).
        var details = workDir is null ? command : $"{command}\n[cwd: {workDir}]";
        if (!await _approval.RequestApprovalAsync("run_command", details, ct, subject: command))
            return Strings.RunCancelled;

        var background = args.Bool("background", false);
        if (background)
        {
            var (cwd, env) = _session.Snapshot();
            var startCwd   = workDir ?? cwd;
            var (dialect, _) = Shell.ShellLauncher.Resolve();
            var script     = ShellStateProtocol.BuildBackgroundScript(dialect, startCwd, env, command);
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
    /// <summary>Kills the background jobs this tool started — they outlive the editor otherwise.</summary>
    public void Dispose() => _background.Dispose();

}
