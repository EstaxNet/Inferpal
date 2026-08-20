using System.IO;
using Inferpal.Services.Shell;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Detached background commands (<c>run_command background:true</c>). Two properties matter beyond
/// the happy path: the captured output must stay bounded (a dev server prints forever), and every
/// job must die with the editor — a child started with <c>UseShellExecute=false</c> outlives its
/// parent on Windows.
/// </summary>
[Collection(ShellSerialCollection.Name)]
public class BackgroundShellRegistryTests
{
    /// <summary>
    /// These jobs are written in PS syntax; they skip silently on a pwsh-less POSIX machine
    /// (every CI matrix runner ships pwsh — the bash contract is covered by PosixShellTests, §23).
    /// </summary>
    private static bool PowerShellAvailable => ShellLauncher.Resolve().Dialect == ShellDialect.PowerShell;

    /// <summary>
    /// Polls until <paramref name="predicate"/> holds over the <b>accumulated</b> output, instead of
    /// sleeping a fixed delay. Accumulation matters: each poll drains the buffer by design, so the
    /// last poll of a finished job legitimately returns nothing new.
    /// </summary>
    private static async Task<(string Output, bool Running, int? ExitCode, bool Found)> PollUntilAsync(
        BackgroundShellRegistry registry, string id, Func<string, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var sb       = new System.Text.StringBuilder();
        var last     = registry.Poll(id);

        while (true)
        {
            sb.Append(last.NewOutput);
            if (predicate(sb.ToString()) || !last.Running || DateTime.UtcNow >= deadline)
                return (sb.ToString(), last.Running, last.ExitCode, last.Found);
            await Task.Delay(25);
            last = registry.Poll(id);
        }
    }

    [Fact]
    public async Task Job_RunsAndReportsItsOutputThenDisappears()
    {
        if (!PowerShellAvailable) return;
        using var registry = new BackgroundShellRegistry();
        var id = registry.Start("Write-Output 'hello-bg'", "echo", Path.GetTempPath());

        var result = await PollUntilAsync(registry, id, out_ => out_.Contains("hello-bg"));

        Assert.True(result.Found);
        Assert.Contains("hello-bg", result.Output);

        // Drained and exited ⇒ the job is eventually forgotten.
        await PollUntilAsync(registry, id, _ => false);
        Assert.False(registry.Poll(id).Found);
    }

    [Fact]
    public async Task Poll_ReturnsOnlyWhatIsNewSinceThePreviousPoll()
    {
        if (!PowerShellAvailable) return;
        using var registry = new BackgroundShellRegistry();
        // "two" is gated on a file the test creates, not on a fixed sleep: on a busy CI runner a
        // 400 ms window was short enough for both lines to land in the very first poll.
        var gate = Path.Combine(Path.GetTempPath(), $"inferpal-bg-gate-{Guid.NewGuid():N}");
        // '' doubles any apostrophe: a profile path like C:\Users\O'Brien would otherwise
        // terminate the single-quoted PowerShell literal and the whole script would fail to parse.
        var id = registry.Start(
            $"Write-Output 'one'; while (-not (Test-Path '{gate.Replace("'", "''")}')) {{ Start-Sleep -Milliseconds 25 }}; Write-Output 'two'",
            "echo twice", Path.GetTempPath());

        try
        {
            var first = await PollUntilAsync(registry, id, out_ => out_.Contains("one"));
            Assert.Contains("one", first.Output);
            Assert.DoesNotContain("two", first.Output);

            // The next poll only carries what appeared since — "one" was already drained.
            File.WriteAllText(gate, "");
            var second = await PollUntilAsync(registry, id, out_ => out_.Contains("two"));
            Assert.Contains("two", second.Output);
            Assert.DoesNotContain("one", second.Output);
        }
        finally { try { File.Delete(gate); } catch { } }
    }

    [Fact]
    public void Stop_KillsTheJobAndForgetsIt()
    {
        if (!PowerShellAvailable) return;
        using var registry = new BackgroundShellRegistry();
        var id = registry.Start("Start-Sleep -Seconds 300", "sleep", Path.GetTempPath());

        Assert.True(registry.Stop(id));
        Assert.False(registry.Stop(id));            // already gone
        Assert.False(registry.Poll(id).Found);
    }

    [Fact]
    public void List_ExposesRunningJobsForDiagnostics()
    {
        if (!PowerShellAvailable) return;
        using var registry = new BackgroundShellRegistry();
        var id = registry.Start("Start-Sleep -Seconds 300", "sleep 300", Path.GetTempPath());

        var jobs = registry.List();

        var job = Assert.Single(jobs);
        Assert.Equal(id, job.Id);
        Assert.Equal("sleep 300", job.Command);
        Assert.True(job.Running);
    }

    [Fact]
    public void Dispose_KillsEveryStillRunningJob()
    {
        // The regression this guards: closing the editor used to leave these powershells behind.
        if (!PowerShellAvailable) return;
        var registry = new BackgroundShellRegistry();
        registry.Start("Start-Sleep -Seconds 300", "a", Path.GetTempPath());
        registry.Start("Start-Sleep -Seconds 300", "b", Path.GetTempPath());
        Assert.Equal(2, registry.List().Count);

        registry.Dispose();

        Assert.Empty(registry.List());
    }
}
