using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Presentation;
using Inferpal.Services.Shell;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What a run leaves running (pari 168): the background jobs a turn started and that still run when it ends are said under
/// its answer, with Stop — only that turn's, never one an earlier turn started; a job that ended on its own says so.
/// </summary>
[Collection(ShellSerialCollection.Name)]
public class LeftRunningTests
{
    private static bool PowerShellAvailable => ShellLauncher.Resolve().Dialect == ShellDialect.PowerShell;

    private static async Task UntilEndedAsync(BackgroundShellRegistry registry, string id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (registry.List().Any(j => j.Id == id && j.Running) && DateTime.UtcNow < deadline) await Task.Delay(25);
    }

    [Fact]
    public void ATurnsJobs_AreTheOnesStartedAfterItsMark_StillRunning()
    {
        if (!PowerShellAvailable) return;
        using var registry = new BackgroundShellRegistry();
        var before = registry.Start("Start-Sleep -Seconds 30", "sleep before", Path.GetTempPath());
        var mark = registry.Mark();
        var first = registry.Start("Start-Sleep -Seconds 30", "npm run dev", Path.GetTempPath());
        var second = registry.Start("Start-Sleep -Seconds 30", "npx vitest --watch", Path.GetTempPath());

        var left = LeftRunning.Since(registry, mark);

        Assert.Equal([new LeftRunningJob(first, "npm run dev"), new LeftRunningJob(second, "npx vitest --watch")], left);
        Assert.DoesNotContain(left, j => j.Id == before);   // an earlier turn's job is not said again
        Assert.Empty(LeftRunning.Since(registry, registry.Mark()));   // a turn that started none left none
        Assert.Empty(LeftRunning.Since(null, 0));                        // no registry (a turn without tools)
    }

    [Fact]
    public async Task Stop_StopsARunningJob_AndSaysAJobThatEndedOnItsOwn_WithItsCode()
    {
        if (!PowerShellAvailable) return;
        using var registry = new BackgroundShellRegistry();
        var mark = registry.Mark();
        var running = registry.Start("Start-Sleep -Seconds 30", "npm run dev", Path.GetTempPath());
        var native = OperatingSystem.IsWindows() ? "cmd /c exit 1" : "sh -c 'exit 1'";
        var ended = registry.Start(ShellStateProtocol.BuildBackgroundScript(ShellDialect.PowerShell, Path.GetTempPath(),
                                                                            new Dictionary<string, string>(), native),
                                   native, Path.GetTempPath());
        await UntilEndedAsync(registry, ended);
        Assert.Contains(registry.List(), j => j.Id == ended && !j.Running);   // witness: it ended on its own
        Assert.Equal([running], LeftRunning.Since(registry, mark).Select(j => j.Id));   // an ended job is not "left running"

        Assert.Equal((BackgroundShellRegistry.StopOutcome.Stopped, (int?)null), registry.StopIfRunning(running));
        Assert.DoesNotContain(registry.List(), j => j.Id == running);
        Assert.Equal((BackgroundShellRegistry.StopOutcome.AlreadyEnded, (int?)1), registry.StopIfRunning(ended));
        Assert.Contains(registry.List(), j => j.Id == ended);   // the model may still read its last output
        Assert.Equal((BackgroundShellRegistry.StopOutcome.AlreadyEnded, (int?)null), registry.StopIfRunning("bg99"));
        // A second Stop of a job just killed finds it gone: the card keeps what the first said (both editors).
        Assert.Equal((BackgroundShellRegistry.StopOutcome.AlreadyEnded, (int?)null), registry.StopIfRunning(running));
    }

    [Fact]
    public void TheCard_SaysEachJob_AndWhatAStopDid()
    {
        Assert.Null(LeftRunning.Card([], "VS Code"));
        Assert.Null(LeftRunning.Text([]));

        var jobs = new List<LeftRunningJob> { new("bg1", "npm run dev"), new("bg2", "npx vitest --watch") };
        var card = LeftRunning.Card(jobs, "VS Code")!;
        Assert.Equal(Strings.LeftRunningTitle, card.Title);
        // One note per number still running: a Stop that leaves one makes it agree.
        Assert.Equal([Strings.LeftRunningNote(1, "VS Code"), Strings.LeftRunningNote(2, "VS Code")], card.Notes);
        Assert.Contains("VS Code", card.Notes[0]);
        Assert.Same(jobs, card.Jobs);

        var text = LeftRunning.Text(jobs)!;
        Assert.Contains("`npm run dev` (bg1)", text);
        Assert.Contains("`npx vitest --watch` (bg2)", text);

        Assert.Equal((Strings.LeftRunningStopped, true), LeftRunning.Outcome((BackgroundShellRegistry.StopOutcome.Stopped, null)));
        Assert.Equal((Strings.LeftRunningEnded(1), true), LeftRunning.Outcome((BackgroundShellRegistry.StopOutcome.AlreadyEnded, 1)));
        Assert.Contains("1", LeftRunning.Outcome((BackgroundShellRegistry.StopOutcome.AlreadyEnded, 1)).Text);
        Assert.Equal((Strings.LeftRunningEndedUnknown, true), LeftRunning.Outcome((BackgroundShellRegistry.StopOutcome.AlreadyEnded, null)));
        // A kill that did not end it is never said stopped, and leaves the row its Stop (still running).
        Assert.Equal((Strings.LeftRunningNotStopped, false), LeftRunning.Outcome((BackgroundShellRegistry.StopOutcome.NotStopped, null)));
        // A PowerShell backtick does not break the ACP sentence's code span.
        Assert.Contains("`` npm run dev `--port 3000 `` (bg3)", LeftRunning.Text([new("bg3", "npm run dev `--port 3000")]));
    }

    [Fact]
    public void ACommand_IsShownOnOneLine_Bounded()
    {
        Assert.Equal("cd web ⏎ npm run dev", LeftRunning.OneLine("cd web\r\n  npm run dev\n"));
        var longOne = LeftRunning.OneLine(new string('x', 500));
        Assert.Equal(200, longOne.Length);
        Assert.EndsWith("…", longOne);
    }

    [Fact]
    public void TheCard_IsNeverSaved()
    {
        Assert.Contains("jobs", Inferpal.Services.Persistence.SessionManager.ScreenOnlyRoles);
    }
}
