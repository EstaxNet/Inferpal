using System.Diagnostics;
using System.Text;

namespace Inferpal.Services;

/// <summary>
/// One-shot <c>git</c> invocation, shared by every front-end.
/// </summary>
/// <remarks>
/// Both adapters needed the same three commands (<c>diff --staged</c>, <c>diff</c>,
/// <c>status --short</c>) to feed <c>/check</c> and <c>/commit</c>; a second copy of the process
/// plumbing would have been the third in the repository. Failures are returned, never thrown: the
/// callers treat "no git here" as an empty diff, which is the correct answer for them.
/// </remarks>
/// <summary>
/// Runs <c>git &lt;args&gt;</c> for a command handler. Injected rather than called directly so the
/// handlers stay testable without a repository, and so each front-end keeps its own root.
/// </summary>
internal delegate Task<(string Output, int ExitCode)> GitRunner(string args, CancellationToken ct);

internal static class GitProcess
{
    /// <summary>Binds a working directory, giving the <see cref="GitRunner"/> the handlers expect.</summary>
    public static GitRunner For(string? workDir) => (args, ct) => RunAsync(args, workDir, ct);

    /// <param name="args">Arguments after <c>git</c>.</param>
    /// <param name="workDir">Working directory; null/empty = the process's own.</param>
    /// <returns>stdout (stderr appended when non-empty) and the exit code, -1 if git never ran.</returns>
    public static async Task<(string Output, int ExitCode)> RunAsync(
        string args, string? workDir, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
            };
            if (!string.IsNullOrEmpty(workDir)) psi.WorkingDirectory = workDir;
            // No credential prompt from a background process, and parseable English output.
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["LANG"]                = "en_US.UTF-8";

            // ⚠ ChildProcess, not Process.Start: without it git inherits the host's stdin — the
            // JSON-RPC pipe in VS Code — allocates a console and hangs at 0 % CPU forever. Measured
            // on 2026-08-03; the same call takes 31 ms from an ordinary process, which is why only
            // the VS Code front-end was affected. See ChildProcess for the full account.
            using var proc = ChildProcess.Start(psi);

            // Both pipes drained CONCURRENTLY. Reading stdout to the end first is a textbook
            // deadlock: a child that fills the stderr buffer meanwhile blocks writing, so it never
            // closes stdout and the first read never completes. git is chatty on stderr — every
            // CRLF warning goes there — so this is reachable, not theoretical.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            await proc.WaitForExitAsync(ct);

            var combined = stdout.Trim();
            if (!string.IsNullOrWhiteSpace(stderr))
                combined += (combined.Length > 0 ? "\n" : "") + stderr.Trim();
            return (combined, proc.ExitCode);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow($"GitProcess({args})", ex);
            return (ex.Message, -1);
        }
    }
}
