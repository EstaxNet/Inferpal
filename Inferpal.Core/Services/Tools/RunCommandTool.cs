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

    /// <summary>The background commands still running — they die with this process.</summary>
    internal IReadOnlyList<(string Id, string Command)> RunningJobs =>
        _background.List().Where(j => j.Running).Select(j => (j.Id, j.Command)).ToList();

    /// <summary>The folder the next command starts in and the environment overrides it inherits, as the session holds them.</summary>
    internal (string Cwd, IReadOnlyDictionary<string, string> Env) SessionState => _session.Snapshot();

    public RunCommandTool(IApprovalService approval, InferpalConfig config, Func<string> root)
    {
        _approval = approval;
        _session  = new ShellSession(root, config);
    }

    public string Name => "run_command";

    // A shell that runs the rest of the line as its own command (cmd /c, bash -c, pwsh -Command…), and dotnet named with its
    // extension or its folder: read through, so the check they wrap is seen.
    private static readonly System.Text.RegularExpressions.Regex ShellWrapper = new(
        @"(?i)(?:^|[;&|(]\s*)(?:\S*[\\/])?(?:cmd(?:\.exe)?\s+/[ck]|(?:ba|z)?sh\s+-l?c|(?:pwsh|powershell)(?:\.exe)?\s+(?:-NoProfile\s+)?-c(?:ommand)?)\s+[""']?",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant, RegexBudget.Default);
    private static readonly System.Text.RegularExpressions.Regex DotnetSpelled = new(
        @"(?i)(?:\S*[\\/])?dotnet\.exe\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant, RegexBudget.Default);

    /// <summary>
    /// Whether <paramref name="command"/> starts a build or a test run (<see cref="Agent.CheckCommand"/>), read through a
    /// wrapping shell and each step of the line (<see cref="Execution.CommandLine"/>). ⚠ A guard, not a boundary: the
    /// runners are a list — <c>dotnet run</c>, <c>msbuild</c>, <c>./build.ps1</c> are not in it.
    /// </summary>
    internal static bool RunsACheck(string command)
    {
        try
        {
            var plain = DotnetSpelled.Replace(ShellWrapper.Replace(command, "; "), "dotnet");
            return Agent.CheckCommand.IsCheck(plain)
                   || Execution.CommandLine.Steps(plain).Any(s => Agent.CheckCommand.IsCheck(s.Trim().Trim('"', '\'')));
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { return Agent.CheckCommand.IsCheck(command); }
    }

    /// <inheritdoc/>
    /// <remarks>Reading a background job's output or the list of jobs runs nothing; a command, or stopping a job, does.</remarks>
    public ToolEffect EffectOf(JsonElement args) =>
        args.Keyword("action") is "poll" or "list" or "wait" ? ToolEffect.Reads : ToolEffect.Runs;

    /// <summary>
    /// How this machine's shell is named to the model, and how it sets an environment variable.
    /// </summary>
    /// <remarks>
    /// ⚠ Read from <see cref="ShellLauncher"/>, never written down. The execution path speaks two
    /// dialects (PowerShell on Windows or where <c>pwsh</c> is on PATH, POSIX otherwise), and this
    /// description is the only thing telling the model <i>how to write the command</i>. Fixed to
    /// "PowerShell", it hands a Windows script to <c>/bin/bash</c> on the published Linux and macOS
    /// builds: the model writes <c>Get-ChildItem</c> and <c>$env:FOO</c>, bash refuses them, and the
    /// agent spends its iteration budget discovering by trial and error what one sentence could have
    /// said.
    /// </remarks>
    private static (string Shell, string SetEnv) Speak()
    {
        var (dialect, fileName) = ShellLauncher.Resolve();
        return (ShellLauncher.SpokenName(dialect, fileName),
                dialect == ShellDialect.PowerShell ? "$env:NAME='…'" : "export NAME=…");
    }

    public string Description
    {
        get
        {
            var (shell, setEnv) = Speak();
            return $"Runs a {shell} command in a persistent session (working directory and environment "
                 + $"variables set by 'cd' or `{setEnv}` persist to later calls). Set background=true for "
                 + "long-running commands (builds, servers, watchers): it returns a job id immediately. Use "
                 + "action='poll' with that id to read new output, action='wait' with 'until' to wait for a line (a server's "
                 + "\"listening\" line) instead of polling again and again, and action='stop' to terminate it.";
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
            action            = new { type = "string", description = "Manage a background job instead of running a command: 'poll' (read new output), 'wait' (until a line holds 'until', the job ends, or timeout_seconds), 'stop' (terminate), or 'list'." },
            id                = new { type = "string", description = "The background job id (returned when background=true). Required for action='poll'/'wait'/'stop'." },
            until             = new { type = "string", description = "For action='wait': the text a line of the job's output must contain (plain text, not a pattern)." },
            timeout_seconds   = new { type = "integer", description = $"For action='wait': the longest to wait, {MinWaitSeconds}-{MaxWaitSeconds} seconds (default {DefaultWaitSeconds})." },
            // ⚠ Short on purpose: this definition goes with every request.
            repeat            = new { type = "integer", description = $"To measure: run it {RepeatedRun.MinRepeat}-{RepeatedRun.MaxRepeat} times after a warm-up; gives the median, the spread, and whether it changed beyond the noise since its last measure." }
        },
        required = Array.Empty<string>()
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (args.Keyword("action") is { } action)
            return action == "wait" ? await WaitAsync(args, ct) : HandleAction(action, args);

        if (args.Str("command") is not { Length: > 0 } command)
            return "Error: 'command' is required (or use action='poll'/'stop'/'list').";

        var rawWorkDir = args.Str("working_directory");
        // A relative working_directory is relative to the SESSION's directory, as the parameter
        // says — never to the process's, which in Visual Studio is the extension host's folder.
        var workDir    = string.IsNullOrWhiteSpace(rawWorkDir) ? null : PathSanitizer.Sanitize(rawWorkDir, _session.CurrentDirectory);

        // ⚠ A folder that does not exist is refused BEFORE the prompt: approved as "[cwd: …]", the command used to run
        // in the session's folder or the workspace root instead — `git clean -fdx` approved for one folder, run in another.
        if (workDir is not null && !System.IO.Directory.Exists(workDir))
            return $"Error: 'working_directory' {workDir} is not an existing folder — nothing ran. Check the path, or omit "
                 + $"it to run in {_session.CurrentDirectory}.";

        // The folder is on the prompt whenever the command does not run at the workspace root — asked for, or where an
        // earlier `cd` left the session: approving "git clean -fdx" with no folder named reads as the root.
        // Permission rules keep matching the raw command (the documented subject for run_command).
        var repeat  = args.Has("repeat") ? ClampedArgument.Read(args, "repeat", RepeatedRun.DefaultRepeat, RepeatedRun.MinRepeat, RepeatedRun.MaxRepeat) : ((int, string?)?)null;
        if (repeat is not null && args.Int("repeat", int.MinValue) == int.MinValue)
            return $"Error: 'repeat' is a number of runs ({RepeatedRun.MinRepeat}-{RepeatedRun.MaxRepeat}), not '{args.Str("repeat")}' — nothing ran.";
        if (repeat is not null && args.Bool("background", false))
            return "Error: 'repeat' measures a command that ends; a background job does not — nothing ran.";
        var runIn   = _session.StartDirectory(workDir).Directory;
        var details = ShellSession.SameFolder(runIn, _session.WorkspaceRoot) ? command : $"{command}\n[cwd: {runIn}]";
        // One card for every run of a measure: it says how many.
        if (repeat is { } times) details += "\n" + Strings.MeasureRunsOnCard(times.Item1 + 1);
        // ⚠ A rule judges the command line, never the folder: `allow run_command ^dotnet test` would run another
        // repository's build outside the workspace without a card. Outside it, the card is always asked.
        var outside = !PathSanitizer.IsUnderRoot(runIn, _session.WorkspaceRoot);
        // A build or a test of a workspace whose own code the person declined to run is forced too — never the person's own
        // /run, which counts as their agreement (WorkspaceCodeConsent), and is recorded as such.
        var declinedCheck = false;
        if (PackageCheck.IsThePersonsOwnCall)
        {
            if (RunsACheck(command)) WorkspaceCodeConsent.NoteThePersonsRun(_session.WorkspaceRoot);
        }
        else
            declinedCheck = WorkspaceCodeConsent.Declined(_session.WorkspaceRoot) && RunsACheck(command);
        if (!await _approval.RequestApprovalAsync("run_command", details, ct, subject: command, forcePrompt: outside || declinedCheck))
            return Strings.RunCancelled;

        var background = args.Bool("background", false);
        if (background)
        {
            var (_, env)   = _session.Snapshot();
            var (startCwd, vanished) = _session.StartDirectory(workDir);
            var (dialect, _) = Shell.ShellLauncher.Resolve();
            var script     = ShellStateProtocol.BuildBackgroundScript(dialect, startCwd, env, command);
            var id         = _background.Start(script, command, startCwd);
            // What the job changes after this call returns happens outside any watch: said by /undo-run, not promised.
            WorkTreeBracket.NoteUncovered(UncoveredReason.Background);
            return (vanished is null ? "" : ShellSession.VanishedNote(vanished, startCwd))
                 + $"Started background job '{id}'. Use action='poll' id='{id}' to read its output, action='stop' id='{id}' to terminate it.";
        }

        if (repeat is { } measure) return await MeasureAsync(command, runIn, measure.Item1, measure.Item2, ct);
        return await _session.RunAsync(command, workDir, ct);
    }

    /// <summary>The last measure of each command in this session — by folder (the shared rule for paths), then by command
    /// line: what the next one compares to.</summary>
    private readonly Dictionary<string, Dictionary<string, TimingSummary>> _measures = new(PathComparer.Default);

    /// <summary>How much of a run's output a measure gives: its END, where a build or a test run says its verdict.</summary>
    private const int MeasureOutputChars = 2000;

    /// <summary>
    /// A command run <paramref name="times"/> times after a warm-up, timed (pari 103): <see cref="RepeatedRun"/> says the
    /// median, the spread, and against this command's previous measure whether a difference is beyond the noise.
    /// </summary>
    /// <remarks>
    /// ⚠ Every run starts in <paramref name="runIn"/>, the folder the card showed: a <c>cd</c> in the command would otherwise move
    /// each next run, out of the workspace with no card.
    /// ⚠ The answer is read by the readers of a command's verdict (<see cref="Agent.CheckCommand.ExitCode"/>, an answer that
    /// starts with "Error:"): a run that did not finish makes the whole answer an error, and the output given is a failed run's
    /// when one failed, its END kept — a measured test run that failed is never read as passed.
    /// </remarks>
    private async Task<string> MeasureAsync(string command, string runIn, int times, string? clampNote, CancellationToken ct)
    {
        var head = clampNote is null ? "" : clampNote + "\n";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var output = await _session.RunAsync(command, runIn, ct);
        var warmUp = watch.Elapsed.TotalSeconds;
        if (Execution.ToolFailure.IsFailure(output))
            return "Error: the warm-up run of the measure did not finish, so nothing was measured.\n" + head + output;
        if (Agent.CheckCommand.ExitCode(output) is not 0)
            return head + "The warm-up run failed, so nothing was measured — fix the command first.\n[output of the warm-up run]\n"
                 + End(output);
        var runs = new List<RunTiming>();
        string? failed = null;
        var failedRun = 0;
        for (var i = 1; i <= times; i++)
        {
            watch.Restart();
            output = await _session.RunAsync(command, runIn, ct);
            if (Execution.ToolFailure.IsFailure(output))
                return $"Error: run {i} of {times} of the measure did not finish, so the measure stopped there.\n" + head
                     + RepeatedRun.Report(runs, warmUp, null) + "\n[output of run " + i + "]\n" + End(output);
            var code = Agent.CheckCommand.ExitCode(output);
            runs.Add(new(watch.Elapsed.TotalSeconds, code));
            if (code is not 0 && failed is null) (failed, failedRun) = (output, i);
        }
        if (!_measures.TryGetValue(runIn, out var byCommand)) _measures[runIn] = byCommand = new(StringComparer.Ordinal);
        byCommand.TryGetValue(command, out var previous);
        if (RepeatedRun.Summarize(runs) is { } now) byCommand[command] = now;
        var shown = failed is null ? $"[output of the last run]\n{End(output)}" : $"[output of run {failedRun}, the first that failed]\n{End(failed)}";
        return head + RepeatedRun.Report(runs, warmUp, previous) + "\n" + shown;
    }

    /// <summary>The end of a run's output, the cut said: a command says its verdict last.</summary>
    private static string End(string output) =>
        output.Length <= MeasureOutputChars ? output
        : $"[… the first {output.Length - MeasureOutputChars} characters of the output are not shown]\n" + output[^MeasureOutputChars..];

    private string HandleAction(string action, JsonElement args)
    {
        switch (action)
        {
            case "poll":
            {
                var id = GetId(args);
                if (id is null) return "Error: action='poll' requires 'id'.";
                var r = _background.Poll(id);
                if (!r.Found) return $"Error: no background job '{id}' (it may have finished and been drained already, or ended "
                                   + "unpolled among too many others: the oldest are let go).";
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
                return $"Error: unknown action '{action}' (expected 'poll', 'wait', 'stop', or 'list').";
        }
    }

    internal const int MinWaitSeconds = 1, MaxWaitSeconds = 300, DefaultWaitSeconds = 60;

    /// <summary>The most output a wait keeps (its newest), whatever the job prints meanwhile.</summary>
    internal const int MaxWaitKept = 200_000;

    /// <summary>
    /// <c>action='wait'</c>: the job's new output up to the first line that holds <c>until</c>, or up to its end, or to the
    /// deadline — said as such, never as a failure: a server slower than the wait is still starting.
    /// </summary>
    /// <remarks>Reads like <c>poll</c> (the offset moves), so nothing read here is read again by the next poll.</remarks>
    private async Task<string> WaitAsync(JsonElement args, CancellationToken ct)
    {
        var id = GetId(args);
        if (id is null) return "Error: action='wait' requires 'id'.";
        var until = args.Str("until");
        if (string.IsNullOrEmpty(until)) return "Error: action='wait' requires 'until' (the text a line must contain).";
        var (seconds, notice) = ClampedArgument.Read(args, "timeout_seconds", DefaultWaitSeconds, MinWaitSeconds, MaxWaitSeconds);
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var seen = new StringBuilder();
        var scanned = 0;   // what was already searched: each read is searched once, never the whole output again
        var dropped = 0;
        // Waiting on a job computes nothing: completion and indexing of the whole account may use the GPU meanwhile.
        using var paused = Hardware.GpuScheduler.PauseThisFlow();
        string Kept() => (dropped > 0 ? $"(the first {dropped} characters of its output were let go)\n" : string.Empty) + seen.ToString().TrimEnd();
        while (true)
        {
            var r = _background.Poll(id);
            if (!r.Found)
                return ClampedArgument.Above(notice, seen.Length == 0 ? $"Error: no background job '{id}'."
                    : seen.ToString().Contains(until, StringComparison.Ordinal) ? $"[job '{id}' — '{until}' appeared; the job is no longer kept]\n{Kept()}"
                    : $"[job '{id}' — no longer kept]\n{Kept()}");
            seen.Append(r.NewOutput);
            // Whole lines only: a line still being written could hold half of the text.
            var text = seen.ToString();
            var complete = r.Running ? text.LastIndexOf('\n') + 1 : text.Length;
            var from = Math.Max(0, scanned - until.Length);
            var at = complete > from ? text.IndexOf(until, from, complete - from, StringComparison.Ordinal) : -1;
            scanned = Math.Max(scanned, complete);
            // ⚠ Everything read is returned: poll moved past it, and what followed the line (an exception after "listening")
            // would be lost to the model.
            if (at >= 0)
                return ClampedArgument.Above(notice, $"[job '{id}' — '{until}' appeared; still {(r.Running ? "running" : "finished")}]\n" + Kept());
            // A chatty job waited on for minutes keeps its newest output, the drop said.
            if (seen.Length > MaxWaitKept)
            {
                var cut = seen.Length - MaxWaitKept;
                seen.Remove(0, cut);
                dropped += cut;
                scanned = Math.Max(0, scanned - cut);
            }
            if (!r.Running)
                return ClampedArgument.Above(notice,
                    $"[job '{id}' — finished (exit code {r.ExitCode?.ToString() ?? "unknown"}) before '{until}' appeared]\n"
                    + (seen.Length == 0 ? "(no new output)" : Kept()));
            if (DateTime.UtcNow >= deadline)
                return ClampedArgument.Above(notice,
                    $"[job '{id}' — still running; '{until}' has not appeared after {seconds} s (it may still be starting: wait again, "
                    + "or poll to read it)]\n" + (seen.Length == 0 ? "(no new output)" : Kept()));
            await Task.Delay(250, ct);
        }
    }

    private static string? GetId(JsonElement args) =>
        args.Str("id") is { Length: > 0 } id ? id : null;
    /// <summary>The jobs this tool started with <c>background: true</c> — what <c>view_window</c> looks at.</summary>
    internal BackgroundShellRegistry Background => _background;

    /// <summary>Kills the background jobs this tool started — they outlive the editor otherwise.</summary>

    public void Dispose() => _background.Dispose();

}
