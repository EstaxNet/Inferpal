using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text;
using Inferpal.Config;

namespace Inferpal.Services.Shell;

/// <summary>
/// A persistent shell "session" for the agent: working directory and environment overrides are
/// preserved across <see cref="RunCommandTool"/> calls even though each command still runs in a
/// fresh, isolated shell process — <c>powershell.exe</c>/<c>pwsh</c> or <c>bash</c> depending on
/// the machine (<see cref="ShellLauncher"/>, §23; see <see cref="ShellStateProtocol"/> for why
/// there is no live REPL pipe). One instance lives for the lifetime of the tool registry
/// (i.e. per workspace).
/// </summary>
internal sealed class ShellSession
{
    private readonly Func<string> _root;
    private readonly InferpalConfig _config;
    private readonly IReadOnlyDictionary<string, string> _baselineEnv;
    private readonly object _lock = new();

    private string? _cwd;
    private Dictionary<string, string> _overrides = new(ShellStateProtocol.EnvNameComparer);

    public ShellSession(Func<string> root, InferpalConfig config)
    {
        _root        = root;
        _config      = config;
        _baselineEnv = CaptureProcessEnv();
    }

    /// <summary>Current working directory of the session (workspace root until the model cd's).</summary>
    public string CurrentDirectory
    {
        get { lock (_lock) return _cwd ?? _root(); }
    }

    /// <summary>The cwd/env overrides a background job should inherit at launch time.</summary>
    public (string Cwd, IReadOnlyDictionary<string, string> Env) Snapshot()
    {
        lock (_lock) return (_cwd ?? _root(), new Dictionary<string, string>(_overrides, ShellStateProtocol.EnvNameComparer));
    }

    /// <summary>
    /// Runs a command in the persistent session: restores cwd/env, executes, then captures the new
    /// cwd/env for the next call. Returns the command output (with a <c>[stderr]</c> section appended
    /// when the command wrote to stderr). Never throws except for user cancellation.
    /// </summary>
    public async Task<string> RunAsync(string command, string? workDirOverride, CancellationToken ct)
    {
        string startCwd;
        IReadOnlyDictionary<string, string> env;
        lock (_lock)
        {
            startCwd = workDirOverride ?? _cwd ?? _root();
            env      = new Dictionary<string, string>(_overrides, ShellStateProtocol.EnvNameComparer);
        }
        if (!Directory.Exists(startCwd))
            startCwd = _root();

        var (dialect, shell) = ShellLauncher.Resolve();
        var marker = ShellStateProtocol.NewMarker();
        var script = ShellStateProtocol.BuildForegroundScript(dialect, startCwd, env, command, marker);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_config.CommandTimeoutSeconds));

        var psi = ShellLauncher.BuildStartInfo(dialect, shell, script);

        using var process = ChildProcess.Start(psi);

        // Bounded, and with no token of their own — both for the same reason as in ChildProcess,
        // which is the twin of this method: killing the tree closes the pipes, so the reads finish
        // by themselves, and a command killed on timeout can still hand back what it printed.
        // Cancelling the reads instead threw that output away, which is exactly what a timeout
        // most needs to show (the build's last lines before it hung).
        var stdoutTask = ChildProcess.ReadCappedAsync(process.StandardOutput);
        var stderrTask = ChildProcess.ReadCappedAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Timeout or user cancel: Process.Dispose() does NOT terminate the native process, so
            // kill the whole tree to avoid leaving an orphaned powershell.exe.
            try { process.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw; // user cancelled — abort the run

            // A bounded wait for the pipes the kill just closed, then report the partial output:
            // the previous version returned the one-line timeout message alone, so a command that
            // ran for its whole budget and printed a thousand useful lines told the model nothing.
            await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask),
                               Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));

            var salvaged = ShellStateProtocol.ParseForeground(Salvage(stdoutTask), marker).Output;
            var timedOut = $"Error: command timed out after {_config.CommandTimeoutSeconds}s.";
            return string.IsNullOrWhiteSpace(salvaged)
                ? timedOut
                : $"{timedOut}\n[output before the timeout]\n{salvaged.TrimEnd()}";
        }

        var rawStdout = await stdoutTask;
        var stderr    = await stderrTask;

        var state = ShellStateProtocol.ParseForeground(rawStdout, marker);
        ApplyState(state);

        var output = state.Output;
        if (!string.IsNullOrWhiteSpace(stderr))
            output += $"\n[stderr]\n{stderr.Trim()}";
        return output;
    }

    /// <summary>What a read produced, or nothing if it has not finished — never a throw.</summary>
    private static string Salvage(Task<string> read) =>
        read.IsCompletedSuccessfully ? read.Result : string.Empty;

    private void ApplyState(ShellRunState state)
    {
        if (!state.StateCaptured) return;
        lock (_lock)
        {
            if (state.Cwd is not null) _cwd = state.Cwd;
            _overrides = ShellStateProtocol.ComputeOverrides(_baselineEnv, state.EnvFull);
        }
    }

    internal static string Encode(string script) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    private static IReadOnlyDictionary<string, string> CaptureProcessEnv()
    {
        var dict = new Dictionary<string, string>(ShellStateProtocol.EnvNameComparer);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            if (e.Key is string k) dict[k] = e.Value?.ToString() ?? string.Empty;
        }
        return dict;
    }
}
