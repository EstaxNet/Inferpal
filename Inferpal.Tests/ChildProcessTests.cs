using System.Diagnostics;
using System.IO;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Child processes must never inherit the parent's stdin (roadmap: defect found 2026-08-03 by
// driving the VS Code host — git hung forever on the JSON-RPC pipe it had been handed as stdin).
public class ChildProcessTests
{
    // ChildProcess is cross-platform Core code, so its guinea-pig children are too: the same
    // behaviours are exercised through cmd/powershell on Windows and /bin/sh on POSIX.
    private static ProcessStartInfo Psi(string windowsCmdArgs, string posixShScript)
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", windowsCmdArgs)
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", posixShScript } };
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError  = true;
        psi.UseShellExecute        = false;
        psi.CreateNoWindow         = true;
        return psi;
    }

    private static ProcessStartInfo ShellPsi(string powershellCommand, string posixShScript) =>
        OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -Command \"{powershellCommand}\"")
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", posixShScript } };

    [Fact]
    public void StdinIsRedirectedAndClosed()
    {
        // The OS-level hang cannot be reproduced in-process: it needs a pipe as the *parent's*
        // stdin, which a test host does not have. What is pinned here is the decision that was
        // missing — the flag — and that the pipe is shut rather than left open.
        using var proc = ChildProcess.Start(Psi("/c exit 0", "exit 0"));

        Assert.True(proc.StartInfo.RedirectStandardInput);
        Assert.Throws<ObjectDisposedException>(() => proc.StandardInput.Write('x'));
    }

    [Fact]
    public async Task AChildThatReadsStdinGetsEofInsteadOfBlockingForever()
    {
        // The behavioural half, and the reason closing beats merely redirecting: a background agent
        // must not be stuck on input nobody will ever type.
        using var proc = ChildProcess.Start(Psi("/c set /p X= & exit 0", "read X; exit 0"));

        var wait  = proc.WaitForExitAsync();
        var first = await Task.WhenAny(wait, Task.Delay(10_000));

        Assert.Same(wait, first);   // the child finished; it did not sit waiting on input
        Assert.True(proc.HasExited);
    }

    // ── RunAsync: the two properties seven private copies did not agree on ──────────

    [Fact]
    public async Task BothPipesAreDrainedConcurrently_SoAFloodOnStderrCannotDeadlock()
    {
        // The defect this pins is not hypothetical: GetGitStatusTool read stdout only, and a child
        // that fills the stderr buffer blocks writing, never closes stdout, and the read of stdout
        // never returns. 200 KB is far past any pipe buffer (typically 4-64 KB).
        var psi = ShellPsi(
            "$s = 'x' * 200000; [Console]::Error.Write($s); [Console]::Out.Write('done')",
            "head -c 200000 /dev/zero | tr '\\0' 'x' 1>&2; printf done");

        var run = await ChildProcess.RunAsync(psi, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(run.TimedOut);
        Assert.Equal("done", run.Stdout);
        Assert.Equal(200_000, run.Stderr.Length);
    }

    [Fact]
    public async Task ATimeoutKillsTheChildAndReportsInsteadOfThrowing()
    {
        var psi = ShellPsi("Start-Sleep -Seconds 20", "sleep 20");

        var started = DateTime.UtcNow;
        var run = await ChildProcess.RunAsync(psi, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.True(run.TimedOut);
        Assert.Equal(-1, run.ExitCode);
        // Killed, not merely abandoned: Process.Dispose() terminates nothing.
        // ⚠ The ceiling stays well UNDER the child's own sleep: at 30 s against a 20 s sleep the
        // assertion is met by the child dying of old age, so a kill that never happened reads the
        // same as one that did.
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task TheCallersOwnCancellationStillThrows()
    {
        // A user pressing stop is not an expired budget, and the two must not arrive as one value.
        var psi = ShellPsi("Start-Sleep -Seconds 20", "sleep 20");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ChildProcess.RunAsync(psi, TimeSpan.FromMinutes(5), cts.Token));
    }

    [Theory]
    [InlineData("out", "", "out")]
    [InlineData("", "err", "err")]
    [InlineData("out\n", "err", "out\nerr")]
    [InlineData("", "", "")]
    public void Combined_JoinsWithoutInventingBlankLines(string stdout, string stderr, string expected)
    {
        // Several callers split this text line by line; a leading or trailing empty line from an
        // unconditional "\n" join used to reach their parsers.
        Assert.Equal(expected, new ChildProcessResult(0, stdout, stderr, TimedOut: false).Combined);
    }

    // ── Plafond de capture ─────────────────────────────────

    [Fact]
    public async Task ReadCapped_KeepsHeadAndTail_AndSaysWhatItDropped()
    {
        // The foreground path read the pipe to the end with no ceiling, while its detached twin
        // (BackgroundShellRegistry) has capped at 512 KB forever, with the reason written on its
        // constant. A `type` of a large file was enough to hold all of it in memory, in UTF-16, in
        // the host process.
        var size    = ChildProcess.MaxCapturedChars * 3;
        var payload = "DEBUT" + new string('x', size) + "FIN";

        var read = await ChildProcess.ReadCappedAsync(new StreamReader(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(payload))));

        Assert.True(read.Length < ChildProcess.MaxCapturedChars + 200,
            $"captured {read.Length} chars for a cap of {ChildProcess.MaxCapturedChars}.");
        // Both ends survive: a parser reads the closing summary (run_tests) or the first error (a
        // compiler). It is the middle that nobody reads.
        Assert.StartsWith("DEBUT", read, StringComparison.Ordinal);
        Assert.EndsWith("FIN", read, StringComparison.Ordinal);
        Assert.Contains("dropped to bound memory", read, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadCapped_LeavesOrdinaryOutputExactlyAsItIs()
    {
        // The cap must cost the common case nothing: no marker, no truncated copy.
        var payload = string.Join('\n', "line 1", "line 2", "Passed!  - Failed: 0, Passed: 12", "");

        var read = await ChildProcess.ReadCappedAsync(new StreamReader(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(payload))));

        Assert.Equal(payload, read);
    }
}
