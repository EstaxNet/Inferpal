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
        // The child says who it is first: the call returns at its budget whether the child was killed or only abandoned,
        // so the return proves nothing — the child gone does.
        // ⚠ Its sleep stays within the suite's cap on a parked child (OrphanedChildTests), and everything this test waits
        // for ends well under that sleep: a child dying of old age must never read as one killed.
        var marker = Path.Combine(Path.GetTempPath(), $"inferpal-child-{Guid.NewGuid():N}.pid");
        var psi = ShellPsi($"Set-Content -Path '{marker}' -Value $PID; Start-Sleep -Seconds 30",
                           $"echo $$ > '{marker}'; exec sleep 30");
        var budget = TimeSpan.FromSeconds(5);
        var started = DateTime.UtcNow;
        var deadline = started + TimeSpan.FromSeconds(25);
        int? child = null;
        try
        {
            var run = await ChildProcess.RunAsync(psi, budget, CancellationToken.None);

            Assert.True(run.TimedOut);
            Assert.Equal(-1, run.ExitCode);
            // The call returns at its budget, not when held pipes let go (a runner that plays two test series at once
            // kills a tree slowly: the suite's slack, never a few seconds).
            Assert.True(DateTime.UtcNow < deadline, $"returned after {(DateTime.UtcNow - started).TotalSeconds:0.0} s");

            Assert.True(File.Exists(marker), "the child never wrote its id within its budget: UNDECIDED, not green.");
            child = int.Parse(File.ReadAllText(marker).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            // Killed, not merely abandoned: Process.Dispose() terminates nothing.
            while (IsOurs(child.Value) && DateTime.UtcNow < deadline) await Task.Delay(100);
            Assert.False(IsOurs(child.Value), "the child outlived its budget: abandoned, not killed");
        }
        finally
        {
            // Only the child itself, recognised by when it started: a number the system gave to another process since
            // is never touched.
            if (child is { } id && IsOurs(id))
                try { using var left = Process.GetProcessById(id); left.Kill(entireProcessTree: true); } catch { /* best-effort cleanup */ }
            try { File.Delete(marker); } catch { /* best-effort cleanup */ }
        }

        // The process with this id is the child: alive, and started between the call and the end of its budget.
        bool IsOurs(int id)
        {
            try
            {
                using var p = Process.GetProcessById(id);
                var since = p.StartTime.ToUniversalTime();
                return !p.HasExited && since >= started.AddSeconds(-1) && since <= started + budget + TimeSpan.FromSeconds(1);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return false;   // gone, or another account's: not ours
            }
        }
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
