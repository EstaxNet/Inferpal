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

    /// <summary>Wall-clock budget for one git invocation.</summary>
    /// <remarks>
    /// git is not supposed to take this long; the budget exists because a repository in a bad state
    /// (a lock left by a crashed client, an unreachable network remote) makes it wait rather than
    /// fail, and an agent turn must not wait with it.
    /// </remarks>
    internal static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The primitive: runs git and hands back both streams separately.
    /// </summary>
    /// <remarks>
    /// Callers that <em>parse</em> git's output (porcelain status, diffs) must not receive stderr
    /// mixed in — a single CRLF warning would corrupt the parse — while callers that <em>show</em>
    /// the outcome want both. Hence the split here and the join in <see cref="RunAsync"/>, instead
    /// of a second copy of the plumbing per need. Throwing is not one of the options: "no git here"
    /// is a legitimate answer, so a failure comes back as exit code -1.
    /// </remarks>
    public static async Task<ChildProcessResult> CaptureAsync(
        string args, string? workDir, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
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
            // the VS Code front-end was affected. It also drains both pipes concurrently, which is
            // the other half of that day's lesson. See ChildProcess for the full account.
            return await ChildProcess.RunAsync(psi, Timeout, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow($"GitProcess({args})", ex);
            return new ChildProcessResult(-1, string.Empty, ex.Message, TimedOut: false);
        }
    }

    /// <param name="args">Arguments after <c>git</c>.</param>
    /// <param name="workDir">Working directory; null/empty = the process's own.</param>
    /// <returns>stdout (stderr appended when non-empty) and the exit code, -1 if git never ran.</returns>
    public static async Task<(string Output, int ExitCode)> RunAsync(
        string args, string? workDir, CancellationToken ct)
    {
        var r = await CaptureAsync(args, workDir, ct);

        var combined = r.Stdout.Trim();
        if (!string.IsNullOrWhiteSpace(r.Stderr))
            combined += (combined.Length > 0 ? "\n" : "") + r.Stderr.Trim();
        return (combined, r.ExitCode);
    }
}
