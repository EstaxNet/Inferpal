using System.Diagnostics;
using System.Text.Json;
using Inferpal.Config;

namespace Inferpal.Services.Tools;

/// <summary>User-defined tool that runs a configurable shell command.</summary>
/// <param name="getRoot">The workspace root the command runs in (empty: none known yet).</param>
internal sealed class UserShellTool(string name, string command, IApprovalService approval, InferpalConfig config,
                                    Func<string> getRoot) : ITool
{
    /// <summary>Where the agent's arguments go in a command that names the spot — what both settings panels tell the
    /// user to write.</summary>
    internal const string ArgsPlaceholder = "{args}";

    private bool HasPlaceholder => command.Contains(ArgsPlaceholder, StringComparison.Ordinal);

    public string Name        => name;
    public string Description => $"User-defined tool. Runs: {command}";
    public object Parameters  => new
    {
        type       = "object",
        properties = new
        {
            args = new
            {
                type        = "string",
                description = HasPlaceholder
                    ? $"Text that replaces {ArgsPlaceholder} in the command."
                    : "Optional extra arguments appended to the command.",
            }
        },
        required = Array.Empty<string>(),
    };

    /// <summary>
    /// The command that runs: <paramref name="extra"/> in place of every <c>{args}</c> when the command names the spot,
    /// appended after it otherwise.
    /// </summary>
    /// <remarks>
    /// ⚠ The settings say "use {args} for arguments". Appended instead, the literal placeholder stays in the command — a
    /// script block to PowerShell, a stray word to bash — and a mid-command one (<c>git grep -n {args} src</c>) searches
    /// for the wrong thing, whose "no match" the model reads as "absent".
    /// </remarks>
    internal static string Expand(string command, string? extra) =>
        command.Contains(ArgsPlaceholder, StringComparison.Ordinal)
            ? command.Replace(ArgsPlaceholder, extra ?? string.Empty, StringComparison.Ordinal)
            : string.IsNullOrEmpty(extra) ? command : $"{command} {extra}";

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var extra   = args.Str("args");
        var fullCmd = Expand(command, extra);

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
            // ⚠ The persistent shell's PowerShell preparation, both halves: a table view (`pwd`, `Get-ChildItem`, a
            // module's own) is cut to the console's width — a path past it ends in "…" — unless the buffer is widened,
            // and a widened table pads every line, which TrimLineEnds takes off.
            var script = dialect == Shell.ShellDialect.PowerShell ? Shell.ShellStateProtocol.WidenConsole + fullCmd : fullCmd;
            var psi = Shell.ShellLauncher.BuildStartInfo(dialect, shell, script);
            // ⚠ In the workspace, like run_command, run_tests and the build: started bare, the command inherits the
            // process's folder — the workspace in VS Code, the extension host's own start folder in Visual Studio, where
            // `dotnet test`, `npm run lint` or `git diff --quiet` answer "no project" / "not a git repository", read as a
            // fact about the user's code.
            var root = getRoot();
            if (!string.IsNullOrEmpty(root) && Directory.Exists(root)) psi.WorkingDirectory = root;

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

            // ⚠ The exit code is the whole answer of a custom tool as often as its output
            // (`git diff --quiet`, a linter that only sets it): the persistent shell's rule — a
            // silent failure must not read as success — holds here too, with the same note.
            var result = Shell.ShellStateProtocol.TrimLineEnds((run.Stdout + run.Stderr).Trim());
            if (run.ExitCode != 0)
                result = (result + Shell.ShellStateProtocol.ExitNote(dialect, fullCmd, run.ExitCode)).TrimStart('\n');
            return string.IsNullOrEmpty(result) ? "(no output)" : result;
        }
        catch (OperationCanceledException) { throw; } // user cancelled
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }
}
