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
            // ⚠ core.quotePath (on by default) prints every path with a byte above 0x7F as octal escapes:
            // Zażółć.cs becomes "Za\305\274\303\263\305\202\304\207.cs", a Chinese name nothing but octal — the name the
            // model reads in get_git_status, /commit and /check, and cannot pass back to read_file. Off, git writes
            // the name in UTF-8, which is how its output is decoded here.
            // ⚠ stdout is captured BYTE-EXACT (Latin-1 maps each byte to one char) and decoded line by line below: git
            // prints a file's content as the bytes the file holds, so a diff of a source saved in a legacy code page,
            // decoded as UTF-8 whole, reached /commit, /check and get_git_status as "caf�" — a line the model
            // cannot quote back into an edit, and that a review reads as corruption.
            var psi = new ProcessStartInfo("git", "-c core.quotePath=false " + args)
            {
                StandardOutputEncoding = Encoding.Latin1,
                StandardErrorEncoding  = Encoding.UTF8,
            };
            if (!string.IsNullOrEmpty(workDir)) psi.WorkingDirectory = workDir;
            // No credential prompt from a background process, and parseable English output.
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["LANG"]                = "en_US.UTF-8";

            // ⚠ ChildProcess, not Process.Start: without it git inherits the host's stdin — the
            // JSON-RPC pipe in VS Code — allocates a console and hangs at 0 % CPU forever, on a
            // call that takes 31 ms elsewhere. It also drains both pipes concurrently, without
            // which a chatty repository deadlocks git. See ChildProcess.
            var run = await ChildProcess.RunAsync(psi, Timeout, ct);
            return run with { Stdout = DecodeCaptured(run.Stdout) };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow($"GitProcess({args})", ex);
            return new ChildProcessResult(-1, string.Empty, ex.Message, TimedOut: false);
        }
    }

    /// <summary>
    /// stdout captured as Latin-1 — one char per byte — decoded line by line; a char above U+00FF can only be the
    /// capture's own truncation marker, and passes through (turned back into a byte it would become "?").
    /// </summary>
    internal static string DecodeCaptured(string latin1)
    {
        var text  = new StringBuilder(latin1.Length);
        var start = 0;
        for (var i = 0; i <= latin1.Length; i++)
        {
            if (i < latin1.Length && latin1[i] <= '\u00FF') continue;
            if (i > start) text.Append(Tools.TextFileEncoding.DecodeLines(Encoding.Latin1.GetBytes(latin1, start, i - start)));
            if (i < latin1.Length) text.Append(latin1[i]);
            start = i + 1;
        }
        return text.ToString();
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

    /// <summary>
    /// The named failure for a runner result, or <c>null</c> when git answered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>THE reader of a <see cref="GitRunner"/> result.</b> The exit code exists so that "no
    /// git here" can be told apart from "nothing changed", and dropping it does not even fail alike
    /// everywhere: <see cref="RunAsync"/> appends stderr to the output, so a refusal arrives as a
    /// NON-empty string — it becomes the diff <c>/commit</c> describes, the diff <c>/check</c>
    /// reviews, and the "recent commit subjects" of the brief written into
    /// <c>.inferpal/context.md</c>, i.e. the system prompt of every session after it.
    /// </para>
    /// <para>
    /// ⚠ <b>A non-zero exit is not always a broken repository, and this is why the gate belongs to
    /// the caller, not here.</b> On a fresh repository <c>log</c> and <c>diff HEAD</c> both answer
    /// 128 because <c>HEAD</c> does not exist yet, and the repository is perfectly healthy. This
    /// method names what happened; where a failure is fatal to the answer is the caller's call.
    /// </para>
    /// </remarks>
    /// <param name="command">git's arguments, for the message — the caller's own spelling.</param>
    public static string? FailureNote(string command, (string Output, int ExitCode) result) =>
        result.ExitCode == 0
            ? null
            : Inferpal.Localization.Strings.GitCommandFailed(command, Detail(result.Output, result.ExitCode));

    /// <summary>
    /// The first non-empty line of git's output — what it says, never a phrase of ours.
    /// </summary>
    /// <remarks>
    /// git follows a refusal with a usage dump (<c>git diff</c> outside a repository prints eight
    /// lines of it): the actionable sentence is the first, and the rest is noise a reader learns to
    /// skip — which is how a gate ends up disarmed.
    /// </remarks>
    public static string FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
            if (line.Trim() is { Length: > 0 } t) return t;
        return string.Empty;
    }

    /// <summary>git's own words, or its exit code when it produced none.</summary>
    internal static string Detail(string output, int exitCode) =>
        FirstLine(output) is { Length: > 0 } said ? said
      : exitCode < 0 ? "git could not be started (is it installed and on PATH?)"
      : $"exit code {exitCode}";
}
