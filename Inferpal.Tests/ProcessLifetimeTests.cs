using System.Diagnostics;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A child process Inferpal starts dies with the process that started it — killed, crashed, or closed.
/// </summary>
/// <remarks>
/// ⚠ The end that matters runs no code (a crash), so it cannot be provoked in the test process without killing the
/// runner. What the kernel does at that moment is the same as what these tests do by hand: the last handle on the job
/// closes (Windows), the watchdog's pipe ends (Linux, macOS). The crash itself is measured end to end by the probe
/// <c>docs/probes/p05/check_residual.py</c>, on the real host.
/// </remarks>
public sealed class ProcessLifetimeTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    // ── Windows: the job ─────────────────────────────────────────────────────

    [Fact]
    public async Task Windows_ABoundChild_AndTheChildItStarted_DieWhenTheJobCloses()
    {
        if (!OperatingSystem.IsWindows()) return;

        var job = ProcessLifetime.CreateKillOnCloseJob(out var failure);
        Assert.True(job is not null, $"No job object: {failure}");

        // The child starts a grandchild of its own AFTER the bind, as a shell or `npx` does, and says which.
        using var child = Process.Start(new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -Command \"$p = Start-Process ping -ArgumentList '-n','120','127.0.0.1' "
            + "-PassThru -WindowStyle Hidden; [Console]::Out.WriteLine($p.Id); [Console]::Out.Flush(); Start-Sleep 120\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        })!;
        Process? grandchild = null;
        try
        {
            Assert.True(ProcessLifetime.Bind(child, job!), "the child could not join the job");

            var line = await child.StandardOutput.ReadLineAsync().WaitAsync(Budget);
            grandchild = Process.GetProcessById(int.Parse(line!.Trim(), System.Globalization.CultureInfo.InvariantCulture));

            // Reference arm: both run while the job is held — a kill would otherwise prove nothing.
            await Task.Delay(500);
            Assert.False(child.HasExited);
            Assert.False(grandchild.HasExited);

            job!.Dispose();   // the last handle closes: what a crash of the owner does

            await child.WaitForExitAsync().WaitAsync(Budget);
            await grandchild.WaitForExitAsync().WaitAsync(Budget);
            Assert.True(child.HasExited && grandchild.HasExited);
        }
        finally
        {
            try { child.Kill(entireProcessTree: true); } catch { }
            try { grandchild?.Kill(); } catch { }
            grandchild?.Dispose();
            job?.Dispose();
        }
    }

    [Fact]
    public void Windows_TheChildProcessFunnel_BindsWhatItStarts_AndAPlainStartDoesNot()
    {
        if (!OperatingSystem.IsWindows()) return;
        var job = ProcessLifetime.Job;
        Assert.NotNull(job);

        using var bound = ChildProcess.Start(Pinger());
        using var plain = Process.Start(Pinger())!;
        try
        {
            Assert.True(ProcessLifetime.IsInJob(bound, job!), "a child started by ChildProcess.Start is not in the job");
            // Reference arm: the reading is not "everything is in a job".
            Assert.False(ProcessLifetime.IsInJob(plain, job!), "a plain Process.Start reads as bound: the reading proves nothing");
        }
        finally
        {
            try { bound.Kill(entireProcessTree: true); } catch { }
            try { plain.Kill(entireProcessTree: true); } catch { }
        }

        static ProcessStartInfo Pinger() =>
            new("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true };
    }

    // ── Linux and macOS: the group and its watchdog ──────────────────────────

    [Fact]
    public async Task Posix_TheWatchdog_KillsTheWholeGroup_WhenItsPipeEnds()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await PythonForTests.IsInstalledAsync()) return;   // UNDECIDED without Python on the machine — never read as a pass

        // A process that leads a group of its own (what the host does at startup) and starts a child in it.
        using var leader = Process.Start(new ProcessStartInfo("python3")
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            ArgumentList =
            {
                "-c",
                "import os, subprocess, time\n"
                + "os.setpgid(0, 0)\n"
                + "c = subprocess.Popen(['sleep', '120'])\n"
                + "print(c.pid, flush=True)\n"
                + "time.sleep(120)\n",
            },
        })!;
        Process? watchdog = null;
        var grandchild = 0;
        try
        {
            // The line comes after setpgid: from here on, the leader's pid IS its group — never the runner's.
            var line = await leader.StandardOutput.ReadLineAsync().WaitAsync(Budget);
            grandchild = int.Parse(line!.Trim(), System.Globalization.CultureInfo.InvariantCulture);

            watchdog = ProcessLifetime.StartGroupWatchdog(leader.Id);

            // Reference arm: while the pipe is held, the group lives.
            await Task.Delay(1000);
            Assert.False(leader.HasExited, "the group died while the watchdog's pipe was still held");
            Assert.True(await IsRunningAsync(grandchild));

            watchdog.StandardInput.Close();   // the host's end of the pipe: what its death closes

            await leader.WaitForExitAsync().WaitAsync(Budget);
            var deadline = DateTime.UtcNow + Budget;
            while (await IsRunningAsync(grandchild) && DateTime.UtcNow < deadline) await Task.Delay(200);
            Assert.False(await IsRunningAsync(grandchild), "the leader's child outlived the group's watchdog");
        }
        finally
        {
            try { leader.Kill(entireProcessTree: true); } catch { }
            if (grandchild > 0) { try { Process.GetProcessById(grandchild).Kill(); } catch { } }
            try { watchdog?.Kill(); } catch { }
            watchdog?.Dispose();
        }
    }

    /// <summary>Running, and not a zombie waiting for a parent that is gone: what <c>ps</c> says.</summary>
    private static async Task<bool> IsRunningAsync(int pid)
    {
        var run = await ChildProcess.RunAsync(
            new ProcessStartInfo("ps", $"-o stat= -p {pid}"), Budget, CancellationToken.None);
        var state = run.Stdout.Trim();
        return state.Length > 0 && !state.StartsWith('Z');
    }
}
