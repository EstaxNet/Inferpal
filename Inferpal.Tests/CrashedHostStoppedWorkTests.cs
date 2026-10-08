using Inferpal.Host;
using Inferpal.Localization;
using Inferpal.Services.Presentation;
using Inferpal.Services.Tasks;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code: a host that CRASHES takes its running work with it too — said from the last list the extension saw, since
/// no one is left to ask.
/// </summary>
/// <remarks>
/// ⚠ A restart asks the host before stopping it (<c>host/runningWork</c>); a crash cannot. The extension keeps the
/// crash wording of that answer, refreshed by its status poll and at the end of each turn, and says it in <c>onCrash</c>.
/// </remarks>
public partial class HostServerTests
{
    [Fact]
    public void StoppedWork_HasACrashWording()
    {
        var crashed = StoppedWork.Notice([ATask("t1", BackgroundTaskState.Running)], [], null, crashed: true);
        Assert.Equal(Strings.StoppedWorkCrashedNotice("- " + Strings.StoppedWorkTask("t1", "objective of t1")), crashed);
        // Reference arm: nothing running, nothing said — crashed or not.
        Assert.Null(StoppedWork.Notice([], [], null, crashed: true));
    }

    [Fact]
    public async Task RunningWork_AlsoGivesTheCrashWording()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var queue = h.Server.CurrentSession!.GetOrCreateTasks(
            async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return BackgroundTaskQueue.TaskRunOutcome.Of(""); },
            _ => { });
        var id = queue.Submit("map the solution");

        var result = await h.Client.InvokeAsync<RunningWorkResult>("host/runningWork");

        Assert.Equal(Strings.StoppedWorkCrashedNotice("- " + Strings.StoppedWorkTask(id!, "map the solution")), result.IfItCrashes);
    }

    [Fact]
    public void TheExtension_SaysTheLastSeenWork_WhenTheHostCrashes()
    {
        var chat = WebviewRebuildTests.TsCode("chatViewProvider.ts");
        Assert.Contains("this.runningIfItCrashes = (await host.runningWork()).ifItCrashes ?? undefined;",
            WebviewRebuildTests.Body(chat, "private async refreshRunningWork("));
        Assert.Contains("this.sayStoppedWork(this.runningIfItCrashes);", WebviewRebuildTests.Body(chat, "onHostCrashed(): void"));
        // Refreshed where the work changes: the status poll, and the end of every turn (a /task, a /docs add).
        Assert.Contains("await this.refreshRunningWork();", WebviewRebuildTests.Body(chat, "private async pollBackendStatus("));
        Assert.Contains("void this.refreshRunningWork();", WebviewRebuildTests.Body(chat, "private finishTurn("));

        var core = WebviewRebuildTests.Body(WebviewRebuildTests.TsCode("extension.ts"), "async function startHostCore(");
        var crash = core.IndexOf("onCrash: () => {", StringComparison.Ordinal);
        Assert.True(crash > 0, "onCrash moved — the rule reads nothing");   // WITNESS
        Assert.True(core.IndexOf("chatView.onHostCrashed();", crash, StringComparison.Ordinal) > crash, "a crash says nothing of what it stopped");
    }
}
