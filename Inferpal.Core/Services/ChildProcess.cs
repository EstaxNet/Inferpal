using System.Diagnostics;

namespace Inferpal.Services;

/// <summary>Outcome of a captured child process run.</summary>
/// <param name="ExitCode">The child's exit code, or -1 when it was killed on timeout.</param>
/// <param name="TimedOut">The wall-clock budget expired; the child was killed, tree included.</param>
internal readonly record struct ChildProcessResult(
    int ExitCode, string Stdout, string Stderr, bool TimedOut)
{
    /// <summary>Both streams, stderr after stdout, with a single separating newline.</summary>
    /// <remarks>
    /// The join every caller was writing by hand, and not all of them the same way. Empty streams
    /// contribute nothing rather than a blank line — several sites parse this text line by line.
    /// </remarks>
    public string Combined =>
        string.IsNullOrWhiteSpace(Stderr) ? Stdout
      : string.IsNullOrWhiteSpace(Stdout) ? Stderr
      : Stdout.TrimEnd() + "\n" + Stderr;

    /// <summary>The child ran and reported success.</summary>
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

/// <summary>
/// Starts a redirected child process that can never inherit the parent's standard input.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists, and it is not a precaution.</b> A child started with
/// <c>UseShellExecute = false</c> and no <c>RedirectStandardInput</c> inherits the parent's stdin
/// handle. In the VS Code host that handle is the <b>JSON-RPC pipe</b> the editor speaks over —
/// and a console program handed a pipe as stdin can allocate a console and stall on it. Measured on
/// 2026-08-03 by driving the host: <c>git diff --staged</c> never returned, a <c>conhost.exe</c>
/// child appeared next to <c>git.exe</c>, and the process sat at 0 % CPU indefinitely. The identical
/// call takes 31 ms from an ordinary process, which is why nothing in Visual Studio ever showed it:
/// there, stdin is devenv's.
/// </para>
/// <para>
/// Every call site in this code base spawns a child, reads its output and never writes to its
/// input, so redirecting stdin and closing it immediately is both the fix and the correct
/// behaviour: a command that decides to read stdin gets EOF and finishes, instead of blocking a
/// background agent forever on input that is never coming.
/// </para>
/// <para>
/// ⚠ Not for a child the caller talks to (MCP over stdio, the LSP server). Those redirect stdin
/// deliberately and own it; they do not come through here.
/// </para>
/// </remarks>
internal static class ChildProcess
{
    /// <summary>
    /// Configures <paramref name="psi"/> so the child cannot inherit stdin, starts it, and closes
    /// the pipe.
    /// </summary>
    public static Process Start(ProcessStartInfo psi)
    {
        psi.RedirectStandardInput = true;

        var process = Process.Start(psi)!;
        CloseInput(process);
        return process;
    }

    /// <summary>
    /// Runs <paramref name="psi"/> to completion, capturing both output streams.
    /// </summary>
    /// <param name="timeout">Wall-clock budget; <c>null</c> waits indefinitely.</param>
    /// <param name="ct">The caller's own cancellation.</param>
    /// <returns>
    /// Exit code and both streams. On timeout: <see cref="ChildProcessResult.TimedOut"/> is set,
    /// the exit code is -1, and whatever the child managed to write is returned rather than lost.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct"/> fired — a user cancellation is the caller's to handle, and is
    /// deliberately not flattened into the same result as an expired budget.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Why one implementation.</b> Seven call sites grew their own copy of "start, drain, wait,
    /// give up", and they did not agree. Two never drained <c>stderr</c> at all — the textbook
    /// deadlock this repository already paid for once in <see cref="GitProcess"/> on 2026-08-03:
    /// a child that fills the stderr buffer blocks writing, therefore never closes stdout,
    /// therefore the read of stdout never completes. Three never killed the process tree on
    /// timeout, so <c>Process.Dispose()</c> left an orphaned <c>dotnet build</c> or
    /// <c>powershell.exe</c> behind. Each copy was a chance to forget one of the two, and the
    /// forgetting is not hypothetical: it is what the review of 2026-08-07 found still live.
    /// </para>
    /// <para>
    /// Both pipes are drained <b>concurrently</b> and with no cancellation of their own: killing
    /// the tree closes them, so the reads complete on their own and a timed-out build still
    /// reports the lines it produced. A bounded wait covers the child that refuses to die.
    /// </para>
    /// </remarks>
    public static async Task<ChildProcessResult> RunAsync(
        ProcessStartInfo psi, TimeSpan? timeout, CancellationToken ct)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError  = true;
        psi.UseShellExecute        = false;
        psi.CreateNoWindow         = true;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) budget.CancelAfter(t);

        using var proc = Start(psi);

        // No token on the reads: the pipes end when the child does, including when we kill it.
        var stdoutTask = ReadCappedAsync(proc.StandardOutput);
        var stderrTask = ReadCappedAsync(proc.StandardError);

        try
        {
            await proc.WaitForExitAsync(budget.Token);
        }
        catch (OperationCanceledException)
        {
            // Dispose() does not terminate the native process; the whole tree goes, or a build
            // server outlives the run that started it.
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }

            // The caller's cancellation is theirs to see; an expired budget is a result.
            ct.ThrowIfCancellationRequested();

            await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
            return new ChildProcessResult(-1, Salvage(stdoutTask), Salvage(stderrTask), TimedOut: true);
        }

        return new ChildProcessResult(proc.ExitCode, await stdoutTask, await stderrTask, TimedOut: false);

        static string Salvage(Task<string> read) =>
            read.IsCompletedSuccessfully ? read.Result : string.Empty;
    }

    /// <summary>
    /// Ceiling on what a single captured stream contributes to memory. Past it the <b>middle</b>
    /// is dropped, never the end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The detached twin of this capture — <c>BackgroundShellRegistry</c> — has been bounded since
    /// it was written, with the reason spelled out on its own constant: "without a ceiling its
    /// buffer is an unbounded leak in the host process". The foreground path, which is the one
    /// almost every command takes, read to the end of the pipe with no ceiling at all: one
    /// <c>type</c> of a large file, one verbose build, one <c>git log -p</c> on a big repository,
    /// and the extension host holds the whole thing in a UTF-16 string — twice, while it is copied.
    /// </para>
    /// <para>
    /// It drops the middle rather than the oldest half (the background twin's rule) because the
    /// two are read differently: a background job is <em>polled</em> forward, so its oldest lines
    /// are the expendable ones, whereas a foreground result is parsed — and every parser in this
    /// repository reads the <b>summary at the end</b> (<c>run_tests</c>) or the <b>first lines</b>
    /// (a compiler's first error). Truncating either end would break a parser; the middle of a
    /// megabyte of output is what nobody reads.
    /// </para>
    /// </remarks>
    internal const int MaxCapturedChars = 1024 * 1024;

    private const string DropMarker = "\n[… middle of the output dropped to bound memory …]\n";

    /// <summary>
    /// Drains <paramref name="reader"/> to the end, keeping at most <see cref="MaxCapturedChars"/>
    /// characters: the head and the tail, with a marker where the middle was. The pipe is always
    /// drained fully — a child whose output we stopped storing must still be able to write, or it
    /// blocks forever on a full buffer, which is the deadlock this class exists to prevent.
    /// </summary>
    internal static async Task<string> ReadCappedAsync(StreamReader reader)
    {
        const int HeadChars  = MaxCapturedChars / 4;
        var       tailChars  = MaxCapturedChars - HeadChars;

        var head    = new System.Text.StringBuilder();
        var tail    = new System.Text.StringBuilder();
        var dropped = 0L;
        var buffer  = new char[16 * 1024];

        while (true)
        {
            var n = await reader.ReadAsync(buffer, 0, buffer.Length);
            if (n == 0) break;

            var offset = 0;
            if (head.Length < HeadChars)
            {
                var take = Math.Min(HeadChars - head.Length, n);
                head.Append(buffer, 0, take);
                offset = take;
            }
            if (offset >= n) continue;

            tail.Append(buffer, offset, n - offset);
            if (tail.Length <= tailChars) continue;

            var excess = tail.Length - tailChars;
            tail.Remove(0, excess);
            dropped += excess;
        }

        return dropped == 0
            ? head.Append(tail).ToString()
            : head.Append(DropMarker.Replace("dropped", $"({dropped:N0} chars) dropped"))
                  .Append(tail).ToString();
    }

    /// <summary>
    /// Same for a caller that needs to build the <see cref="Process"/> itself (event handlers wired
    /// before start): configure, start, then hand it here.
    /// </summary>
    public static void CloseInput(Process process)
    {
        // Best-effort: a child that exited between Start and here has no pipe left, and that is not
        // a failure — the input is closed either way, which is all this guarantees.
        try { process.StandardInput.Close(); }
        catch (Exception ex) { Diagnostics.Swallow("ChildProcess.CloseInput", ex); }
    }
}
