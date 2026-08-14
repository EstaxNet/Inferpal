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
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

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
