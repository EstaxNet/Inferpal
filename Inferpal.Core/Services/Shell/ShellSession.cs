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
/// there is no live REPL pipe). One instance lives for the lifetime of the tool registry — in Visual Studio, a whole
/// devenv session, during which the solution and so the workspace root can change: the state belongs to the root it was
/// captured under, and is dropped when the root moves.
/// </summary>
internal sealed class ShellSession
{
    private readonly Func<string> _root;
    private readonly InferpalConfig _config;
    private readonly IReadOnlyDictionary<string, string> _baselineEnv;
    private readonly object _lock = new();

    private string? _cwd;
    private Dictionary<string, string> _overrides = new(ShellStateProtocol.EnvNameComparer);
    // The workspace root _cwd and _overrides were captured under (null: nothing captured yet).
    private string? _stateRoot;

    public ShellSession(Func<string> root, InferpalConfig config)
    {
        _root        = root;
        _config      = config;
        _baselineEnv = CaptureProcessEnv();
    }

    /// <summary>Current working directory of the session (workspace root until the model cd's).</summary>
    public string CurrentDirectory
    {
        get { lock (_lock) { DropIfRootMoved(); return _cwd ?? _root(); } }
    }

    /// <summary>The workspace root the session starts from.</summary>
    internal string WorkspaceRoot => _root();

    /// <summary>Whether two paths name the same folder, as the file system compares them.</summary>
    internal static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), PathComparer.Comparison);

    /// <summary>
    /// Forgets the folder and the environment captured under another workspace root. Called under <see cref="_lock"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ Opening another solution in Visual Studio moves the root under this session: without this, the next command
    /// runs in the previous solution's folder — <c>dotnet build</c>, <c>git commit -am</c> — while the system prompt names
    /// the new root.
    /// </remarks>
    private void DropIfRootMoved()
    {
        if (_stateRoot is null || SameFolder(_stateRoot, _root())) return;
        _cwd       = null;
        _overrides = new(ShellStateProtocol.EnvNameComparer);
        _stateRoot = null;
    }

    /// <summary>The cwd/env overrides a background job should inherit at launch time.</summary>
    /// <summary>
    /// Where a command starts: the folder asked for, else the session's — and when that folder no longer exists (deleted
    /// under the session), the workspace root, with the folder that is gone (<c>Vanished</c>) so the output can say so.
    /// </summary>
    internal (string Directory, string? Vanished) StartDirectory(string? workDirOverride)
    {
        string dir;
        lock (_lock) { DropIfRootMoved(); dir = workDirOverride ?? _cwd ?? _root(); }
        return System.IO.Directory.Exists(dir) ? (dir, null) : (_root(), dir);
    }

    /// <summary>The line above a command's output when it did not run where the session was: read as the session's
    /// folder, its output describes another one.</summary>
    internal static string VanishedNote(string gone, string ranIn) =>
        $"[{gone}{VanishedMiddle}{ranIn}]\n";

    private const string VanishedMiddle = " no longer exists — this ran in ";

    /// <summary>
    /// What the command printed, without the notes the session writes around it: the vanished folder above
    /// (<see cref="VanishedNote"/>), the output a background process held open below (<see cref="ChildProcess.OutputHeldOpenNote"/>).
    /// </summary>
    /// <remarks>⚠ A reader of the command's own end — its exit note, its "Error:" — reads through these: the exit code
    /// stood before the held-open note and was read as 0, and a timeout under the vanished note was a run that passed.</remarks>
    internal static string WithoutNotes(string output)
    {
        if (output.StartsWith('[') && output.IndexOf('\n') is var eol and > 0
            && output.IndexOf(VanishedMiddle, 0, eol, StringComparison.Ordinal) > 0 && output[eol - 1] == ']')
            output = output[(eol + 1)..];
        return output.EndsWith(ChildProcess.OutputHeldOpenNote, StringComparison.Ordinal)
            ? output[..^ChildProcess.OutputHeldOpenNote.Length]
            : output;
    }

    public (string Cwd, IReadOnlyDictionary<string, string> Env) Snapshot()
    {
        lock (_lock)
        {
            DropIfRootMoved();
            return (_cwd ?? _root(), new Dictionary<string, string>(_overrides, ShellStateProtocol.EnvNameComparer));
        }
    }

    /// <summary>
    /// Runs a command in the persistent session: restores cwd/env, executes, then captures the new
    /// cwd/env for the next call. Returns the command output (with a <c>[stderr]</c> section appended
    /// when the command wrote to stderr). Never throws except for user cancellation.
    /// </summary>
    public async Task<string> RunAsync(string command, string? workDirOverride, CancellationToken ct)
    {
        IReadOnlyDictionary<string, string> env;
        lock (_lock) { DropIfRootMoved(); env = new Dictionary<string, string>(_overrides, ShellStateProtocol.EnvNameComparer); }
        var (startCwd, vanished) = StartDirectory(workDirOverride);
        var note = vanished is null ? "" : VanishedNote(vanished, startCwd);

        var (dialect, shell) = ShellLauncher.Resolve();
        var marker = ShellStateProtocol.NewMarker();
        var script = ShellStateProtocol.BuildForegroundScript(dialect, startCwd, env, command, marker);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(ChildProcess.CommandDeadlineSeconds(_config)));

        var psi = ShellLauncher.BuildStartInfo(dialect, shell, script);

        using var process = ChildProcess.Start(psi);

        // Bounded, and with no token of their own — both for the same reason as in ChildProcess,
        // which is the twin of this method: killing the tree closes the pipes, so the reads finish
        // by themselves, and a command killed on timeout can still hand back what it printed.
        // Cancelling the reads instead threw that output away, which is exactly what a timeout
        // most needs to show (the build's last lines before it hung).
        var stdout = new ChildProcess.PipeCapture(process.StandardOutput);
        var stderr = new ChildProcess.PipeCapture(process.StandardError);
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
            // the one-line timeout message alone tells the model nothing about a command that ran
            // for its whole budget and printed a thousand useful lines.
            await Task.WhenAny(Task.WhenAll(stdout.Completion, stderr.Completion),
                               Task.Delay(ChildProcess.PipeGraceAfterExit, CancellationToken.None));

            var salvaged = WithStderr(
                ShellStateProtocol.TrimLineEnds(ShellStateProtocol.ParseForeground(stdout.Snapshot(), marker).Output),
                stderr.Snapshot());
            return note + ChildProcess.TimedOutMessage(ChildProcess.CommandDeadlineSeconds(_config), salvaged);
        }

        // The shell exited; a background process it started may still hold the pipes (see
        // ChildProcess.DrainAfterExitAsync). The state lines were written before the exit.
        var drained = await ChildProcess.DrainAfterExitAsync(stdout, stderr, ct);

        var state = ShellStateProtocol.ParseForeground(stdout.Snapshot(), marker);
        ApplyState(state, keepFolder: workDirOverride is not null);

        var output = WithStderr(ShellStateProtocol.TrimLineEnds(state.Output), stderr.Snapshot());   // a widened buffer pads tables
        // The wrapper's own shell always exits 0 (a finally, a trailing printf): the command's code
        // travels in the state block, and a silent failure (`git diff --quiet`) must not read as success.
        // ⚠ Except when the command calls `exit` (`test -f x || exit 1`, `exec ./server`): that ends
        // the wrapper before its state block (POSIX) or without $LASTEXITCODE seeing it (PowerShell),
        // and the shell's own exit code is then the only place the command's code is left.
        var rc = state.ExitCode is { } reported and not 0 ? reported : process.ExitCode;
        if (rc != 0)
            output += ShellStateProtocol.ExitNote(dialect, command, rc);
        if (!drained)
            output += ChildProcess.OutputHeldOpenNote;
        return note + output;
    }

    /// <summary>The command's output with what it wrote to stderr after it — the one way both paths say it.</summary>
    /// <remarks>⚠ The timeout path too: a command killed at its budget is most often a build or a test run whose
    /// errors went to stderr (cargo, pytest's tracebacks, npm), and the salvage kept stdout alone.</remarks>
    private static string WithStderr(string output, string rawStderr)
    {
        var stderrText = PowerShellStderr.Decode(rawStderr);
        return string.IsNullOrWhiteSpace(stderrText) ? output : $"{output}\n[stderr]\n{stderrText.Trim()}";
    }

    /// <param name="keepFolder">The command ran in a <c>working_directory</c> of its own: "for this command", as the
    /// parameter says — taken as the session's folder, every later command runs there, under a prompt that names none.</param>
    private void ApplyState(ShellRunState state, bool keepFolder)
    {
        if (!state.StateCaptured) return;
        lock (_lock)
        {
            if (state.Cwd is not null && !keepFolder) _cwd = state.Cwd;
            _overrides = ShellStateProtocol.ComputeOverrides(_baselineEnv, state.EnvFull);
            _stateRoot = _root();
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
