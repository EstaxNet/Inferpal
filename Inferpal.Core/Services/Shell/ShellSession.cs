using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text;
using Inferpal.Config;

namespace Inferpal.Services.Shell;

/// <summary>
/// A persistent shell "session" for the agent: working directory and environment overrides are
/// preserved across <see cref="RunCommandTool"/> calls even though each command still runs in a
/// fresh, isolated <c>powershell.exe</c> (see <see cref="ShellStateProtocol"/> for why). One
/// instance lives for the lifetime of the tool registry (i.e. per workspace).
/// </summary>
internal sealed class ShellSession
{
    private readonly Func<string> _root;
    private readonly InferpalConfig _config;
    private readonly IReadOnlyDictionary<string, string> _baselineEnv;
    private readonly object _lock = new();

    private string? _cwd;
    private Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);

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
        lock (_lock) return (_cwd ?? _root(), new Dictionary<string, string>(_overrides, StringComparer.OrdinalIgnoreCase));
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
            env      = new Dictionary<string, string>(_overrides, StringComparer.OrdinalIgnoreCase);
        }
        if (!Directory.Exists(startCwd))
            startCwd = _root();

        var marker = ShellStateProtocol.NewMarker();
        var script = ShellStateProtocol.BuildForegroundScript(startCwd, env, command, marker);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_config.CommandTimeoutSeconds));

        var psi = new ProcessStartInfo
        {
            FileName               = "powershell.exe",
            Arguments              = $"-NoProfile -NonInteractive -EncodedCommand {Encode(script)}",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
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
            return $"Error: command timed out after {_config.CommandTimeoutSeconds}s.";
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
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            if (e.Key is string k) dict[k] = e.Value?.ToString() ?? string.Empty;
        }
        return dict;
    }
}
