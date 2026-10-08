using System.IO;
using Inferpal.Host;
using Inferpal.Localization;
using Inferpal.Services.Presentation;
using Inferpal.Services.Tasks;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A VS Code host restart says what it stopped: a background task, a background command, a documentation indexing
/// pass — all live in the host process only, and died with it without a word.
/// </summary>
/// <remarks>
/// The report of a <c>/task</c> never came, the dev server started as <c>bg1</c> was gone, the docs source stayed at 0
/// pages. The extension asks the host (<c>host/runningWork</c>) just before it stops it, and says the answer in the thread.
/// </remarks>
public partial class HostServerTests
{
    private static BackgroundTaskSnapshot ATask(string id, BackgroundTaskState state) =>
        new(id, $"objective of {id}", state, DateTimeOffset.Now, null, null, null, null, [], 0);

    [Fact]
    public void StoppedWork_NamesWhatStillRuns_AndNothingFinished()
    {
        var notice = StoppedWork.Notice(
            [ATask("t1", BackgroundTaskState.Running), ATask("t2", BackgroundTaskState.Queued), ATask("t3", BackgroundTaskState.Succeeded)],
            [("bg1", "npm run dev")],
            "react");

        Assert.NotNull(notice);
        Assert.Contains(Strings.StoppedWorkTask("t1", "objective of t1"), notice);
        Assert.Contains(Strings.StoppedWorkTask("t2", "objective of t2"), notice);
        Assert.DoesNotContain("t3", notice);
        Assert.Contains(Strings.StoppedWorkCommand("bg1", "npm run dev"), notice);
        Assert.Contains(Strings.StoppedWorkDocs("react"), notice);
    }

    /// <summary>Reference arm: a restart with nothing running says nothing.</summary>
    [Fact]
    public void StoppedWork_SaysNothing_WhenNothingRuns()
    {
        Assert.Null(StoppedWork.Notice([ATask("t1", BackgroundTaskState.Failed)], [], null));
    }

    [Fact]
    public async Task RunningWork_NamesABackgroundTaskStillRunning()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        Assert.Null((await h.Client.InvokeAsync<RunningWorkResult>("host/runningWork")).Notice);   // reference arm

        var queue = h.Server.CurrentSession!.GetOrCreateTasks(
            async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return BackgroundTaskQueue.TaskRunOutcome.Of(""); },
            _ => { });
        var id = queue.Submit("index the whole solution");

        var result = await h.Client.InvokeAsync<RunningWorkResult>("host/runningWork");

        Assert.Contains(Strings.StoppedWorkTask(id!, "index the whole solution"), result.Notice);
    }

    [Fact]
    public void TheExtension_AsksBeforeItStopsTheHost_AndSaysItAfter()
    {
        var core = WebviewRebuildTests.Body(WebviewRebuildTests.TsCode("extension.ts"), "async function startHostCore(");
        var ask  = core.IndexOf("const stopped = await stoppedWorkNotice(host);", StringComparison.Ordinal);
        var stop = core.IndexOf("await host.stop();", StringComparison.Ordinal);
        var say  = core.IndexOf("chatView.sayStoppedWork(stopped);", StringComparison.Ordinal);
        Assert.True(stop > 0, "the activator no longer stops a host here: the rule reads nothing");   // WITNESS
        Assert.True(ask >= 0 && ask < stop, "the running work is asked after the host is gone");
        Assert.True(say > stop, "what the restart stopped is not said");
    }
}
