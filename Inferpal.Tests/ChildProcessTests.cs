using System.Diagnostics;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Child processes must never inherit the parent's stdin (roadmap: defect found 2026-08-03 by
// driving the VS Code host — git hung forever on the JSON-RPC pipe it had been handed as stdin).
public class ChildProcessTests
{
    private static ProcessStartInfo Psi(string args) =>
        new("cmd.exe", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

    [Fact]
    public void StdinIsRedirectedAndClosed()
    {
        // The OS-level hang cannot be reproduced in-process: it needs a pipe as the *parent's*
        // stdin, which a test host does not have. What is pinned here is the decision that was
        // missing — the flag — and that the pipe is shut rather than left open.
        using var proc = ChildProcess.Start(Psi("/c exit 0"));

        Assert.True(proc.StartInfo.RedirectStandardInput);
        Assert.Throws<ObjectDisposedException>(() => proc.StandardInput.Write('x'));
    }

    [Fact]
    public async Task AChildThatReadsStdinGetsEofInsteadOfBlockingForever()
    {
        // The behavioural half, and the reason closing beats merely redirecting: a background agent
        // must not be stuck on input nobody will ever type.
        using var proc = ChildProcess.Start(Psi("/c set /p X= & exit 0"));

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
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -Command \"" +
            "$s = 'x' * 200000; [Console]::Error.Write($s); [Console]::Out.Write('done')\"");

        var run = await ChildProcess.RunAsync(psi, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(run.TimedOut);
        Assert.Equal("done", run.Stdout);
        Assert.Equal(200_000, run.Stderr.Length);
    }

    [Fact]
    public async Task ATimeoutKillsTheChildAndReportsInsteadOfThrowing()
    {
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 60\"");

        var started = DateTime.UtcNow;
        var run = await ChildProcess.RunAsync(psi, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.True(run.TimedOut);
        Assert.Equal(-1, run.ExitCode);
        // Killed, not merely abandoned: three call sites used to leave the tree running because
        // Process.Dispose() does not terminate anything.
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task TheCallersOwnCancellationStillThrows()
    {
        // A user pressing stop is not an expired budget, and the two must not arrive as one value.
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 60\"");

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
}
