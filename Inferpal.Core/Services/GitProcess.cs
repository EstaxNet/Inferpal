using System.Diagnostics;
using System.IO;
using System.Text;

namespace Inferpal.Services;

/// <summary>
/// Runs <c>git &lt;args&gt;</c> for a command handler. Injected rather than called directly so the
/// handlers stay testable without a repository, and so each front-end keeps its own root.
/// </summary>
internal delegate Task<(string Output, int ExitCode)> GitRunner(string args, CancellationToken ct);

/// <summary>
/// One-shot <c>git</c> invocation, shared by every front-end.
/// </summary>
/// <remarks>
/// Both adapters needed the same three commands (<c>diff --staged</c>, <c>diff</c>,
/// <c>status --short</c>) to feed <c>/check</c> and <c>/commit</c>; a second copy of the process
/// plumbing would have been the third in the repository. Failures are returned, never thrown: the
/// callers treat "no git here" as an empty diff, which is the correct answer for them.
/// </remarks>
internal static class GitProcess
{
    /// <summary>Binds a working directory, giving the <see cref="GitRunner"/> the handlers expect.</summary>
    public static GitRunner For(string? workDir) => (args, ct) => RunAsync(args, workDir, ct);

    /// <summary>The runner of <c>/commit-exec</c>: a commit runs the repository's hooks, with their own budget.</summary>
    /// <remarks>⚠ Under the 15 s read budget, a pre-commit hook that formats, lints or builds — or a GPG passphrase typed
    /// slowly — was killed mid-run: the commit failed every time, and a kill during a post-commit hook reported a commit
    /// that did happen as failed. Stop still cancels it.</remarks>
    public static GitRunner ForCommit(string? workDir) => (args, ct) => RunAsync(args, workDir, ct, CommitTimeout);

    /// <summary>Budget of a commit, hooks included.</summary>
    internal static TimeSpan CommitTimeout => TimeSpan.FromMinutes(10);

    /// <summary>Wall-clock budget for one git invocation.</summary>
    /// <remarks>
    /// git is not supposed to take this long; the budget exists because a repository in a bad state
    /// (a lock left by a crashed client, an unreachable network remote) makes it wait rather than
    /// fail, and an agent turn must not wait with it.
    /// </remarks>
    internal static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A <c>diff</c> in the one shape its readers parse — sections opened by <c>diff --git a/… b/…</c>, paths from the
    /// repository's top, no colour, no external tool — whatever the user's git configuration; any other command as is.
    /// </summary>
    /// <remarks>
    /// ⚠ The readers cut a diff by its section headers — the ignore files' filter drops an excluded file's section by
    /// them — and <c>diff.external</c> (difftastic), <c>color.ui=always</c>, <c>diff.relative</c>, <c>diff.noprefix</c> or
    /// custom prefixes leave no header to find: without this shape the whole diff, excluded files included, reaches the
    /// model. Old gits ignore a configuration key they do not know.
    /// </remarks>
    internal static string DiffShaped(string args)
    {
        var trimmed = args.TrimStart();
        if (trimmed != "diff" && !trimmed.StartsWith("diff ", StringComparison.Ordinal)) return args;
        return "-c color.ui=never -c color.diff=never -c diff.relative=false -c diff.noprefix=false -c diff.mnemonicPrefix=false "
             + "diff --no-ext-diff --src-prefix=a/ --dst-prefix=b/" + trimmed["diff".Length..];
    }

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
        string args, string? workDir, CancellationToken ct, TimeSpan? budget = null)
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
            var psi = new ProcessStartInfo("git", "-c core.quotePath=false " + DiffShaped(args))
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
            var run = await ChildProcess.RunAsync(psi, budget ?? Timeout, ct);
            return run with { Stdout = DecodeCaptured(run.Stdout, workingTree: WorkingTreeEncoding(workDir)) };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow($"GitProcess({args})", ex);
            return new ChildProcessResult(-1, string.Empty, ex.Message, TimedOut: false);
        }
    }

    /// <summary>
    /// Runs git and hands back stdout as the bytes git wrote — a file's content, NUL-separated listings — whole, or
    /// with why it is not (<see cref="GitBytes.Failure"/>): git refused, did not start, ran out of its budget, or wrote
    /// more than <paramref name="maxBytes"/>.
    /// </summary>
    /// <param name="args">Arguments after <c>git</c>, one per element: never joined into a command line, so a path with
    /// a space or a quote stays one argument.</param>
    /// <param name="environment">Variables set for this git only.</param>
    /// <remarks>
    /// ⚠ Not <see cref="CaptureAsync"/>: its capture keeps the head and the tail of a long output with the middle cut
    /// (<see cref="ChildProcess.MaxCapturedChars"/>) and decodes it as text — right for a diff someone reads, fatal for
    /// the content of a file that is going to be written back. Here an output is whole or refused.
    /// </remarks>
    public static async Task<GitBytes> CaptureBytesAsync(
        IReadOnlyList<string> args, string workDir, CancellationToken ct, long maxBytes,
        IReadOnlyDictionary<string, string>? environment = null, TimeSpan? budget = null)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory       = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotePath=false");
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["LANG"]                = "en_US.UTF-8";
        if (environment is not null)
            foreach (var (name, value) in environment) psi.Environment[name] = value;

        Process proc;
        try { proc = ChildProcess.Start(psi); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new GitBytes(null, $"git did not start ({ex.Message})", NotStarted: true);
        }
        using (proc)
        {
            // ⚠ The budget KILLS: a read on an anonymous pipe does not honour a token under Windows, so a git that waits
            // without writing (a lock, a network share) would hold the call for ever. Killing closes the pipes, and the
            // reads below end on their own — no token on them, as in ChildProcess.RunAsync.
            var allowed = budget ?? Timeout;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(allowed);
            using var kill = deadline.Token.Register(() =>
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            });
            // stderr drained beside stdout: a child that fills one pipe while nobody reads it never finishes the other.
            var stderr   = proc.StandardError.ReadToEndAsync(CancellationToken.None);
            var output   = new MemoryStream();
            var buffer   = new byte[81920];
            var overflow = false;
            var stdout   = proc.StandardOutput.BaseStream;
            int read;
            while ((read = await stdout.ReadAsync(buffer, CancellationToken.None)) > 0)
            {
                if (output.Length + read > maxBytes)
                {
                    overflow = true;
                    try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                    break;
                }
                output.Write(buffer, 0, read);
            }
            var exited = true;
            try { await proc.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) { exited = false; }
            // ⚠ An exited child is not a closed pipe: a process it started may still hold stderr. Bounded.
            await Task.WhenAny(stderr, Task.Delay(ChildProcess.PipeGraceAfterExit, CancellationToken.None));
            ct.ThrowIfCancellationRequested();

            if (overflow)                                   return new GitBytes(null, $"git wrote more than {maxBytes} bytes", Overflow: true);
            if (!exited || deadline.IsCancellationRequested) return new GitBytes(null, $"git gave no answer within {allowed.TotalSeconds:0}s and was stopped");
            if (proc.ExitCode != 0)
                return new GitBytes(null, $"git exited with code {proc.ExitCode}"
                    + (stderr.IsCompletedSuccessfully && FirstLine(stderr.Result) is { Length: > 0 } why ? $": {why}" : string.Empty));
            return new GitBytes(output.ToArray(), null);
        }
    }

    // Per work tree: where git keeps a path of its own (`--git-path`). ⚠ `.git` is a file in a linked worktree and in a
    // submodule, and some of its paths live in the common directory: only git can say where they are.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> GitPaths =
        new(PathComparer.Default);

    /// <summary>
    /// The full path git uses for <paramref name="name"/> (<c>info/exclude</c>, say) in the work tree at
    /// <paramref name="workTree"/>; <c>null</c> when git cannot say. Asked once per work tree and name.
    /// </summary>
    public static async Task<string?> GitPathAsync(string workTree, string name, CancellationToken ct)
    {
        var key = workTree + "\0" + name;
        if (GitPaths.TryGetValue(key, out var known)) return known;
        var r = await CaptureBytesAsync(["rev-parse", "--git-path", name], workTree, ct, maxBytes: 64 * 1024);
        var path = r.Output is { } bytes && Encoding.UTF8.GetString(bytes).Trim() is { Length: > 0 } line
            ? Path.GetFullPath(Path.Combine(workTree, line))
            : null;
        GitPaths[key] = path;
        return path;
    }

    /// <summary>
    /// stdout captured as Latin-1 — one char per byte — and decoded here; a char above U+00FF can only be the capture's
    /// own truncation marker, and passes through (turned back into a byte it would become "?").
    /// </summary>
    /// <param name="legacy">The code page of a file that is not UTF-8; the machine's own when null.</param>
    /// <param name="workingTree">The encoding of a working-tree file, by its path in git's output; null = unknown.</param>
    /// <remarks>
    /// <para>
    /// git prints a file's content as the bytes the file holds, between header lines of its own in UTF-8. A line that
    /// is not UTF-8 is in the legacy code page — but a line that IS UTF-8 may be too: 92 of the 500 most common
    /// Chinese characters in GBK (一 为 时 要 小 …) are valid UTF-8 alone, so "// 一" came out as "// һ". One line is
    /// too little evidence, so the content of a file's diff is decided by SIDE: the removed lines are the file's old
    /// version, the added lines its new one, the context both. A side with a line that is not UTF-8 is legacy; failing
    /// that, the working-tree file — read whole, which is decisive — says what the new side is, and the old side
    /// follows it. A conversion to UTF-8 therefore reads right on both sides.
    /// </para>
    /// <para>
    /// ⚠ Line by line is safe only because 0x0A never occurs inside a character of an ASCII-compatible code page, the
    /// double-byte ones (GBK, Big5, Shift_JIS, CP949) included. UTF-16 is not ASCII-compatible — "上" is 0A 4E — and
    /// git never prints it: a UTF-16 file is a binary file to git ("Binary files … differ"), or, with
    /// <c>working-tree-encoding</c>, its content arrives converted to UTF-8.
    /// </para>
    /// </remarks>
    internal static string DecodeCaptured(string latin1, Encoding? legacy = null, Func<string, Encoding?>? workingTree = null)
    {
        legacy ??= Tools.TextFileEncoding.LegacyEncoding;
        var text  = new StringBuilder(latin1.Length);
        var start = 0;
        for (var i = 0; i <= latin1.Length; i++)
        {
            if (i < latin1.Length && latin1[i] <= 0xFF) continue;
            if (i > start) text.Append(DecodeOutput(Encoding.Latin1.GetBytes(latin1, start, i - start), legacy, workingTree));
            if (i < latin1.Length) text.Append(latin1[i]);
            start = i + 1;
        }
        return text.ToString();
    }

    private enum LineKind { Other, Header, Old, New, Both }

    /// <summary>What a file's diff says about its encoding, side by side.</summary>
    private sealed class FileDiff
    {
        public string? Path;
        public bool OldNotUtf8, NewNotUtf8;
        private bool? _newLegacy;

        /// <summary>The new side: a line of it that is not UTF-8, else the working-tree file.</summary>
        public bool NewLegacy(Func<string, Encoding?>? workingTree) =>
            _newLegacy ??= NewNotUtf8 || (Path is not null && workingTree?.Invoke(Path) is { } encoding && IsLegacy(encoding));

        /// <summary>The old side: a line of it that is not UTF-8, else what the new side is.</summary>
        public bool OldLegacy(Func<string, Encoding?>? workingTree) => OldNotUtf8 || NewLegacy(workingTree);

        private static bool IsLegacy(Encoding encoding) => encoding is not (UTF8Encoding or UnicodeEncoding or UTF32Encoding);
    }

    private static string DecodeOutput(byte[] bytes, Encoding legacy, Func<string, Encoding?>? workingTree)
    {
        var lines = new List<(int Start, int Length, LineKind Kind, FileDiff? File)>();
        FileDiff? file = null;
        var inHunks = false;
        for (var start = 0; start < bytes.Length;)
        {
            var newline = Array.IndexOf(bytes, (byte)'\n', start);
            var end     = newline < 0 ? bytes.Length : newline + 1;
            var line    = bytes.AsSpan(start, end - start);

            LineKind kind;
            if (line.StartsWith("diff --git "u8) || line.StartsWith("diff --cc "u8) || line.StartsWith("diff --combined "u8))
            {
                file    = new FileDiff();
                inHunks = false;
                kind    = LineKind.Header;
            }
            else if (file is null)
                kind = LineKind.Other;
            else if (!inHunks)
            {
                inHunks = line.StartsWith("@@"u8);
                kind    = inHunks ? LineKind.Both : LineKind.Header;   // the hunk header quotes the file: its context
                if (line.StartsWith("+++ "u8)) file.Path = PathOf(line[4..]);
            }
            else
            {
                kind = line.IsEmpty ? LineKind.Other : line[0] switch
                {
                    (byte)'+' => LineKind.New,
                    (byte)'-' => LineKind.Old,
                    (byte)' ' => LineKind.Both,
                    (byte)'@' => LineKind.Both,
                    (byte)'\\' => LineKind.Header,   // "\ No newline at end of file"
                    _          => LineKind.Other,
                };
                if (kind == LineKind.Other) file = null;
            }

            if (file is not null && kind is LineKind.Old or LineKind.New or LineKind.Both
                && !System.Text.Unicode.Utf8.IsValid(line))
            {
                if (kind is LineKind.Old or LineKind.Both) file.OldNotUtf8 = true;
                if (kind is LineKind.New or LineKind.Both) file.NewNotUtf8 = true;
            }
            lines.Add((start, end - start, kind, kind == LineKind.Other ? null : file));
            start = end;
        }

        var text = new StringBuilder(bytes.Length);
        foreach (var (start, length, kind, owner) in lines)
        {
            var line = bytes.AsSpan(start, length);
            var inLegacy = !System.Text.Unicode.Utf8.IsValid(line) || (owner is not null && !Ascii.IsValid(line) && kind switch
            {
                LineKind.Old  => owner.OldLegacy(workingTree),
                LineKind.New  => owner.NewLegacy(workingTree),
                LineKind.Both => owner.OldLegacy(workingTree) || owner.NewLegacy(workingTree),
                _             => false,   // headers: paths, which git writes in UTF-8
            });
            text.Append((inLegacy ? legacy : Tools.TextFileEncoding.Utf8NoBom).GetString(line));
        }
        return text.ToString();
    }

    /// <summary>The path on a <c>+++ b/…</c> line, without git's prefix; <c>null</c> for <c>/dev/null</c>.</summary>
    private static string? PathOf(ReadOnlySpan<byte> rest)
    {
        // A path with a space ends with a tab on this line; the line itself with a newline.
        var path = Encoding.UTF8.GetString(rest).TrimEnd('\n', '\r', '\t');
        if (path == "/dev/null") return null;
        // "b/" by default; "w/", "i/", "c/", "o/" under diff.mnemonicPrefix.
        return path.Length > 2 && path[1] == '/' && "bwico".Contains(path[0]) ? path[2..] : path;
    }

    /// <summary>
    /// The git work tree holding <paramref name="dir"/> — its nearest folder with a <c>.git</c> — or <c>null</c>.
    /// </summary>
    /// <remarks>⚠ <c>.git</c> is a FOLDER in a clone, a FILE in a worktree (<c>git worktree add</c>) and a submodule.
    /// Looking for the folder only climbs past such a work tree to the repository above it: the status, the diff and
    /// the snapshot folder of ANOTHER checkout. The one reader of this question.</remarks>
    internal static string? WorkTreeOf(string dir)
    {
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
            if (IsWorkTreeRoot(d.FullName)) return d.FullName;
        return null;
    }

    /// <summary>Whether <paramref name="dir"/> itself is the top of a git work tree (see <see cref="WorkTreeOf"/>).</summary>
    internal static bool IsWorkTreeRoot(string dir)
    {
        var dotGit = Path.Combine(dir, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }

    /// <summary>
    /// The encoding of the working-tree file a path of git's output names — the paths are relative to the repository's
    /// top level, found from <paramref name="workDir"/>; <c>null</c> when there is no repository or no such file.
    /// </summary>
    private static Func<string, Encoding?>? WorkingTreeEncoding(string? workDir)
    {
        try
        {
            if (WorkTreeOf(string.IsNullOrEmpty(workDir) ? Environment.CurrentDirectory : workDir) is { } root)
            {
                return path =>
                {
                    var full = Path.Combine(root, path);
                    return File.Exists(full) ? Tools.TextFileEncoding.Detect(full) : null;
                };
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Diagnostics.Swallow("GitProcess.WorkingTreeEncoding", ex);
        }
        return null;
    }

    /// <param name="args">Arguments after <c>git</c>.</param>
    /// <param name="workDir">Working directory; null/empty = the process's own.</param>
    /// <returns>stdout (stderr appended when non-empty) and the exit code, -1 if git never ran.</returns>
    public static async Task<(string Output, int ExitCode)> RunAsync(
        string args, string? workDir, CancellationToken ct, TimeSpan? budget = null)
    {
        var r = await CaptureAsync(args, workDir, ct, budget);

        var combined = r.Stdout.Trim();
        if (!string.IsNullOrWhiteSpace(r.Stderr))
            combined += (combined.Length > 0 ? "\n" : "") + r.Stderr.Trim();
        // ⚠ Stopped at its budget, git answers -1 like a git that never started, and Detail named the second ("is it
        // installed and on PATH?") — a remedy that cannot apply. Said first, it is the line every reader shows.
        if (r.TimedOut)
            combined = $"git gave no answer within {(budget ?? Timeout).TotalSeconds:0}s and was stopped."
                     + (combined.Length > 0 ? "\n" + combined : string.Empty);
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

/// <summary>
/// What <see cref="GitProcess.CaptureBytesAsync"/> got: the whole output, or why not — and whether that is because git
/// could not start at all, or wrote more than was allowed.
/// </summary>
internal readonly record struct GitBytes(byte[]? Output, string? Failure, bool NotStarted = false, bool Overflow = false);
