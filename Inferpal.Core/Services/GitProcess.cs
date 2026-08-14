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

            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
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
