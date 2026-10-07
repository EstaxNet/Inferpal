using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;

namespace Inferpal.Services.CodeActions;

/// <summary>
/// Runs a quick build / typecheck after a file write and returns any compilation <em>errors</em>
/// (warnings are ignored) as a formatted string, or <c>null</c> when disabled, the file type has no
/// validator, no project root is found, the toolchain is absent, or the build is clean.
/// </summary>
/// <remarks>
/// Polyglot: the ecosystem is chosen by <see cref="BuildValidators"/> from the edited file's
/// extension (built-in .NET / TypeScript / Rust / Go defaults, plus the workspace
/// <c>.inferpal/validators.json</c> overlay). The .NET path is unchanged (parses <c>": error XX:"</c>
/// lines); other ecosystems use the process exit code. Never throws — a failed check returns
/// <c>null</c> so the write tool is never broken.
/// </remarks>
internal sealed class SmartFixValidator
{
    private readonly InferpalConfig    _config;
    private readonly Func<string?>     _getWorkspaceRoot;
    private readonly IApprovalService? _approval;

    /// <summary>Workspace commands approved during this session — asked once, not once per file.</summary>
    private readonly HashSet<string> _approvedCommands = new(StringComparer.Ordinal);

    /// <param name="approval">
    /// Required to run a command coming from <c>.inferpal/validators.json</c>. When absent, such a
    /// command is <b>refused</b> rather than run unattended: no approval surface, no execution.
    /// </param>
    public SmartFixValidator(
        InferpalConfig config, Func<string?>? getWorkspaceRoot = null, IApprovalService? approval = null)
    {
        _config           = config;
        _getWorkspaceRoot = getWorkspaceRoot ?? (() => null);
        _approval         = approval;
    }

    /// <summary>Asks once per session for a repository-authored build command.</summary>
    private async Task<bool> ApproveWorkspaceCommandAsync(string command, CancellationToken ct)
    {
        if (_approvedCommands.Contains(command)) return true;

        if (_approval is null)
        {
            Diagnostics.Record("Permission",
                $"Refused workspace validator (no approval surface): {command}");
            return false;
        }

        bool approved;
        try
        {
            approved = await _approval.RequestApprovalAsync(
                "smart_fix_validator", command, ct, subject: command, diff: null, forcePrompt: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Never let the approval path break a file write — but never fall through to running
            // the command either.
            Diagnostics.Swallow("SmartFixValidator.Approval", ex);
            return false;
        }

        if (approved) _approvedCommands.Add(command);
        else Diagnostics.Record("Permission", $"Declined workspace validator: {command}");
        return approved;
    }

    /// <summary>
    /// The toolchain itself could not start (npx/cargo/go/tsc not installed), as opposed to a build that failed —
    /// reported silently (null), so an unconfigured machine is not told of "errors" that are "tool not installed".
    /// </summary>
    /// <remarks>
    /// ⚠ Judged on what the SHELL says about the command, never on words a build may print: "No such file", "could not
    /// be found", "ENOENT" are how a C compiler reports a missing header, rustc a missing include_str!, a build script
    /// a missing file — real errors, silenced as "tool not installed". And the shell's own sentence is translated
    /// ("n'est pas reconnu", "wurde nicht … erkannt", "Befehl nicht gefunden"): a list of English and French words
    /// reported a missing toolchain as a failed build after every write, in every other language. Kept: exit 127
    /// (POSIX shells) and 9009 (cmd), PowerShell's error id — never translated, printed under the NormalView that
    /// <see cref="ValidatorScript"/> asks for — and npm's own sentences, which npm writes in English only.
    /// </remarks>
    internal static bool IsToolMissing(int exitCode, string output) =>
        exitCode is 127 or 9009
        || output.Contains("CommandNotFoundException", StringComparison.Ordinal)
        || output.Contains("npx canceled due to missing packages", StringComparison.Ordinal)
        || output.Contains("could not determine executable to run", StringComparison.Ordinal);

    public Task<string?> ValidateAsync(string writtenFilePath, CancellationToken ct) =>
        ResolveTarget(writtenFilePath) is { } target
            ? RunTargetAsync(target, ct)
            : Task.FromResult<string?>(null);

    /// <summary>Distinct checks a single batch may run before it stops and says so.</summary>
    /// <remarks>
    /// A coordinated refactor across ten projects would otherwise spend ten builds inside one tool
    /// call. Three is a budget, not a belief about repositories — and reaching it is reported.
    /// </remarks>
    internal const int MaxBatchTargets = 3;

    /// <summary>
    /// Validates every distinct check a batch of written files calls for — once each.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Written for <c>apply_edits</c>, whose whole purpose is to write several files at once.</b>
    /// Validating <c>changed[0]</c> alone lets the rest go: a Core+Tests refactor, or a
    /// <c>.cs</c> + <c>.ts</c> one — which do not even share a validator — is written, reported as
    /// applied, and half of it never compiles, beneath a Smart Fix note that reads as "the build is
    /// fine". Deduplicated by <b>what would actually run</b> (command + directory), so the ordinary
    /// batch of several files in one project still builds exactly once.
    /// </remarks>
    public async Task<string?> ValidateManyAsync(IReadOnlyList<string> writtenFilePaths, CancellationToken ct)
    {
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();
        var skipped = 0;

        foreach (var path in writtenFilePaths)
        {
            if (ResolveTarget(path) is not { } target) continue;
            if (!seen.Add(target.ProjectDir + "\n<>\n" + target.Command)) continue;
            if (seen.Count > MaxBatchTargets) { skipped++; continue; }

            if (await RunTargetAsync(target, ct) is { Length: > 0 } note) notes.Add(note);
        }

        if (skipped > 0)
            notes.Add(Strings.SmartFixBatchCapped(MaxBatchTargets, skipped));

        return notes.Count == 0 ? null : string.Join("\n\n", notes);
    }

    /// <summary>What a written file would make this validator run, or <c>null</c> when nothing.</summary>
    private readonly record struct Target(BuildValidator Validator, string ProjectDir, string Command);

    private Target? ResolveTarget(string writtenFilePath)
    {
        if (!_config.SmartFixEnabled) return null;

        var validators = BuildValidators.Resolve(LoadOverlay());
        var match = BuildValidators.Match(writtenFilePath, validators, FindMarker);
        if (match is null) return null;

        var (validator, projectDir, projectFile) = match.Value;
        return new Target(validator, projectDir, validator.Command.Replace("{project}", projectFile));
    }

    private async Task<string?> RunTargetAsync(Target target, CancellationToken ct)
    {
        var (validator, projectDir, command) = target;

        // Safety: never auto-run a catastrophic command sourced from a (possibly committed)
        // validators.json. Shares the built-in hard denylist with the approval policy (axe 1).
        if (PermissionPolicy.IsHardDenied(command)) return null;

        // ⚠ A validator from `.inferpal/validators.json` was written by the REPOSITORY, and Smart
        // Fix runs it by itself after a write — so cloning a repository and letting the agent touch
        // one file would execute that repository's command, silently. The denylist is no defence
        // here: it matches text, and obfuscation walks around it by construction. The frontier is
        // the approval prompt, where the human reads the raw command, so that is where this goes —
        // force-prompted, because no consent the user gave their own agent covers a stranger's
        // command. Built-in validators (dotnet, tsc, cargo, go) are ours and keep running silently.
        if (validator.FromWorkspace && !await ApproveWorkspaceCommandAsync(command, ct))
            return null;

        try
        {
            var (exitCode, output, timedOut) = await Run(command, projectDir, ct);
            return Interpret(exitCode, output, validator.UseDotnetErrorFilter, timedOut);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // user cancelled the agent run
        }
        catch (OperationCanceledException)
        {
            return Strings.SmartFixTimeout;
        }
        catch (Exception ex)
        {
            // Never crash the write tool — but a check that could not run is not "no validator": silent, the edit read
            // as verified when nothing was.
            Diagnostics.Swallow("SmartFixValidator.Run", ex);
            return Strings.SmartFixCouldNotRun(Diagnostics.RootMessage(ex));
        }
    }

    /// <summary>
    /// The build verdict an edit tool's answer carries: <c>true</c> when a Smart Fix note there names compilation
    /// errors, <c>false</c> when the build passed, <c>null</c> when no build ran (no note, a timeout, a missing tool).
    /// </summary>
    /// <remarks>
    /// ⚠ This note is a build check like <c>run_tests</c> and <c>get_diagnostics</c>, and the turn's last check is read
    /// to warn the user when the answer claims success over a failing build (<c>ChatTurnPolicy.LastCheckFailed</c>):
    /// a turn that ended on "🔨 Smart Fix: 4 compilation error(s)" after an edit said nothing. Read from the producer's
    /// own sentences, in the language they were written in — never from a copy.
    /// </remarks>
    internal static bool? ReadVerdict(string toolOutput)
    {
        if (string.IsNullOrEmpty(toolOutput)) return null;
        const int CountSentinel = 918273645;
        var errorsLine = "^" + Regex.Escape(Strings.SmartFixBuildErrors(CountSentinel, string.Empty).Split('\n')[0].TrimEnd('\r'))
                                    .Replace(CountSentinel.ToString(), @"\d+") + "$";
        var okLine = Strings.SmartFixBuildOk.Trim();
        bool? verdict = null;
        foreach (var raw in toolOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            try
            {
                if (Regex.IsMatch(line, errorsLine, RegexOptions.None, RegexBudget.Default)) return true;   // one project red: red
            }
            catch (RegexMatchTimeoutException) { }
            if (line == okLine) verdict = false;
        }
        return verdict;
    }

    /// <summary>The note for a validator run that ended with <paramref name="exitCode"/>; null stays silent.</summary>
    /// <remarks>
    /// The .NET error lines only NAME the errors; whether the build passed is the exit code's to say, as for
    /// every other toolchain. A build killed on timeout (-1) or dying without a <c>: error XX:</c> line prints
    /// none, and "no error line" is not "built".
    /// </remarks>
    internal static string? Interpret(int exitCode, string output, bool dotnetFilter, bool timedOut = false)
    {
        // The fuse blew: nothing was proven either way, and the partial output is not a diagnosis.
        if (timedOut) return Strings.SmartFixTimeout;

        // .NET: errors only — warnings don't warrant a fix iteration.
        if (dotnetFilter && GetDiagnosticsTool.OutputHasBuildErrors(output))
        {
            var errors = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(l => GetDiagnosticsTool.ErrorLineRegex.IsMatch(l))
                .Select(l => l.Trim())
                .Distinct()
                .ToList();
            return Strings.SmartFixBuildErrors(errors.Count, Listed(errors));
        }

        // The exit code is the reliable failure signal across toolchains.
        if (exitCode == 0) return Strings.SmartFixBuildOk;
        if (IsToolMissing(exitCode, output)) return null;   // toolchain absent → stay silent

        var lines = ExtractErrorLines(output);
        // ⚠ "0 compilation error(s)" is not a sentence:
        // the build failed and named nothing, which is a different thing to go and look at.
        return lines.Count == 0
            ? Strings.SmartFixBuildFailedNoErrors
            : Strings.SmartFixBuildErrors(lines.Count, Listed(lines));
    }

    /// <summary>Error lines rendered into the note. The COUNT that goes with them is the count of
    /// the full list, never of this slice.</summary>
    /// <remarks>
    /// ⚠ Counted AFTER `.Take(...)`, in a message that states "{0} compilation error(s) detected",
    /// eighty errors reach the model as "20" — not a silence, a WRONG NUMBER, in the loop that runs
    /// after EVERY write. The model fixes its twenty, rebuilds, finds sixty, and reads those as
    /// errors it has just introduced.
    /// </remarks>
    internal const int MaxErrorLinesListed = 25;

    /// <summary>Joins at most <see cref="MaxErrorLinesListed"/> lines and says what it left out.</summary>
    internal static string Listed(List<string> lines)
    {
        var text = string.Join("\n", lines.Take(MaxErrorLinesListed));
        return lines.Count > MaxErrorLinesListed
            ? text + $"\n… +{lines.Count - MaxErrorLinesListed} more"
            : text;
    }

    // Prefer lines that look like compiler errors ("error" anywhere); fall back to all non-empty
    // lines (e.g. Go's `file.go:10:5: msg` format has no "error" keyword).
    internal static List<string> ExtractErrorLines(string output)
    {
        var all = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        var errorish = all.Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).ToList();
        return errorish.Count > 0 ? errorish : all;
    }

    private string? FindMarker(string dir, string glob)
    {
        try { return Directory.GetFiles(dir, glob, SearchOption.TopDirectoryOnly).FirstOrDefault(); }
        catch { return null; }
    }

    /// <summary>Overlay text whose rejections were last recorded.</summary>
    private string? _reportedOverlay;

    private IReadOnlyList<BuildValidator> LoadOverlay()
    {
        var root = _getWorkspaceRoot();
        if (string.IsNullOrEmpty(root)) return [];
        var path = Path.Combine(root, ".inferpal", "validators.json");
        try
        {
            if (!File.Exists(path)) return [];
            var text = File.ReadAllText(path);

            // The overlay is re-read after every write; its rejections are recorded once per content,
            // or the same message would fill the diagnostics ring.
            var report = !string.Equals(text, _reportedOverlay, StringComparison.Ordinal);
            _reportedOverlay = text;
            return BuildValidators.ParseConfig(text, report ? r => Diagnostics.Record("ValidatorsOverlay", r) : null);
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("ValidatorsOverlayRead", ex);
            return [];
        }
    }

    // Runs the command line under the machine's shell (resolved like run_command — powershell.exe
    // was hard-coded, so validators silently failed on the published Linux/macOS hosts), in the
    // project directory, with a 60s fuse. Returns (exit code, output).
    /// <summary>The script a validator runs: under PowerShell, with the error view that prints the error id.</summary>
    /// <remarks>pwsh 7's default ConciseView prints a missing command as a translated sentence and no id.</remarks>
    internal static string ValidatorScript(Shell.ShellDialect dialect, string command) =>
        dialect == Shell.ShellDialect.PowerShell ? "$ErrorView = 'NormalView'\n" + command : command;

    /// <summary>What runs a validator's command in its project folder: <see cref="RunAsync"/>, replaced by tests.</summary>
    internal Func<string, string, CancellationToken, Task<(int ExitCode, string Output, bool TimedOut)>> Run { get; init; } = RunAsync;

    private static async Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(string command, string workDir, CancellationToken ct)
    {
        var (dialect, shell) = Shell.ShellLauncher.Resolve();
        var psi = Shell.ShellLauncher.BuildStartInfo(dialect, shell, ValidatorScript(dialect, command));
        psi.WorkingDirectory = workDir;

        var run = await ChildProcess.RunAsync(psi, TimeSpan.FromSeconds(60), ct);
        run = run with { Stderr = Shell.PowerShellStderr.Decode(run.Stderr) };   // CLIXML → text

        // A validator that hangs is a failed validation, not a cancelled edit: -1 with the partial
        // output lets the caller reject the write and show why, where a thrown cancellation reads
        // as if the user had stopped it.
        //
        // ⚠ And the FACT travels with it. Flattened to -1 alone, a killed build reaches Interpret
        // with a partial output carrying no `: error XX:` line and comes out as "N compilation
        // error(s)" — restore lines presented to the model as compilation errors, an empty
        // output as "0 compilation error(s)", and Strings.SmartFixTimeout
        // unreachable.
        return (run.TimedOut ? -1 : run.ExitCode, run.Combined, run.TimedOut);
    }
}
