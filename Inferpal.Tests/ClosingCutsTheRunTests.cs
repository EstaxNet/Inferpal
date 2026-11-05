using System.IO;
using Inferpal.Host;
using Inferpal.Models;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A run the editor's closing stopped is a cut run, and the next start says so. An editor that closes normally stops its
/// turn, and the turn's end closes the run: without the closing told first, the run reads as finished and nothing is said.
/// </summary>
/// <remarks>The process that wrote the run is this test, alive: the run still reads cut, never as a turn running elsewhere,
/// which would refuse its undo and silence the notice.</remarks>
public partial class HostServerTests
{
    /// <summary>A real turn that has written a file and waits for its model; <paramref name="stop"/> ends it, and the run is
    /// read back as the next start reads it.</summary>
    private static async Task<HistoryRun> RunOfATurnStoppedBy(Func<Harness, Task> stop)
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var history = h.Server.CurrentSession!.Tools.History;
        var file = Path.Combine(h.RootDir, "A.cs");
        await File.WriteAllTextAsync(file, "a0\n");

        h.Fake.OnChat = async (onToken, ct) =>
        {
            await history.BackUpBeforeChangeAsync(file, CancellationToken.None);
            await File.WriteAllTextAsync(file, "a0\na1\n", CancellationToken.None);
            onToken?.Invoke("working");
            await Task.Delay(Timeout.Infinite, ct);
            return new ChatTurnResult(string.Empty, null, 0, 0);
        };
        var send = h.Client.InvokeWithParameterObjectAsync<ChatSendResult>("chat/send", new { prompt = "go", agentMode = false });
        await h.Target.FirstToken.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        await stop(h);
        // Answered after the turn's finally wrote the run; a session gone with the pipe fails the answer, not the run.
        try { await send.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs)); }
        catch (StreamJsonRpc.RemoteInvocationException) { }

        var next = new FileHistoryService { WorkspaceRoot = () => h.RootDir, UserHistoryRoot = history.UserHistoryRoot };
        return Assert.Single(next.Runs);
    }

    [Fact]
    public async Task ARunTheEditorsClosingStopped_StaysOpenOnDisk()
    {
        var run = await RunOfATurnStoppedBy(async h =>
        {
            // As VS Code sends them: two notifications, nothing awaited between them.
            await h.Client.NotifyAsync("editor/closing");
            await h.Client.NotifyAsync("shutdown");
        });
        Assert.True(run.CutShort);
        Assert.False(run.OpenElsewhere);
        Assert.NotNull(Services.Presentation.AwayNotice.Build([run], run.Editor!, "VS Code", [], new HashSet<string>(), DateTime.Now)?.Run);
    }

    /// <summary>The editor's process crashed, a remote connection dropped, an ACP client closed its stdin: no message came, and
    /// the turn was cut all the same.</summary>
    [Fact]
    public async Task ARunThePipesDeathStopped_StaysOpenOnDisk()
    {
        var run = await RunOfATurnStoppedBy(h => { h.Server.Dispose(); return Task.CompletedTask; });
        Assert.True(run.CutShort);
    }

    /// <summary>Reference arm: a Stop ends the run like any other.</summary>
    [Fact]
    public async Task ARunAPersonStopped_IsClosedAsEver()
    {
        var run = await RunOfATurnStoppedBy(h => h.Client.InvokeAsync("chat/cancel"));
        Assert.False(run.OpenElsewhere);
        Assert.False(run.CutShort);
    }

    /// <summary>Reference arm: a restart of the host happens in front of the person, and "when VS Code closed" would be false.</summary>
    [Fact]
    public async Task ARunAHostRestartStopped_IsClosedAsEver()
    {
        var run = await RunOfATurnStoppedBy(h => h.Client.NotifyAsync("shutdown"));
        Assert.False(run.OpenElsewhere);
        Assert.False(run.CutShort);
    }

    /// <summary>Only VS Code's closing tells the host it is one: a restart of the host goes through the same stop. The
    /// closing is told first: the shutdown cancels the turn, and the turn's end reads it.</summary>
    [Fact]
    public void VsCode_TellsTheHostItIsClosing_OnlyWhenItCloses()
    {
        var extension = WebviewRebuildTests.TsCode("extension.ts");
        Assert.Contains("host?.stop(true)", WebviewRebuildTests.Body(extension, "export async function deactivate("));
        Assert.Contains("await host.stop();", WebviewRebuildTests.Body(extension, "async function startHostCore("));

        var stop = WebviewRebuildTests.Body(WebviewRebuildTests.TsCode("hostClient.ts"), "async stop(");
        var told = stop.IndexOf("if (closing) conn?.sendNotification('editor/closing')", StringComparison.Ordinal);
        Assert.InRange(told, 0, stop.IndexOf("sendNotification('shutdown')", StringComparison.Ordinal));
    }

    /// <summary>Visual Studio has no closing message: it goes by the extension's dispose, told before anything else is torn
    /// down, and by the command's own token, which a Stop never cancels, at the one place every turn starts. Both, because
    /// neither order is promised.</summary>
    [Fact]
    public void VisualStudio_SaysTheClosing_ByItsDisposeAndByTheCommandsToken()
    {
        string Code(params string[] path) => ConventionCoverageTests.CodeOnly(Path.Combine([ConventionCoverageTests.RepoRoot(), "Inferpal", .. path]));

        var dispose = Code("InferpalExtension.cs");
        var told = dispose.IndexOf("History.CutCurrentRun()", StringComparison.Ordinal);
        Assert.InRange(told, 0, dispose.IndexOf("KillAllServers()", StringComparison.Ordinal));

        var turn = WebviewRebuildTests.Body(Code("ToolWindow", "InferpalToolWindowData.UiHelpers.cs"),
                                            "private CancellationTokenSource BeginOwnedTurn(");
        Assert.Contains("ct.Register(_tools.History.CutCurrentRun)", turn);
    }

    /// <summary>The mark is on the run: the window can go with the process alive, and a later run that ends well is not said
    /// to be cut. A cut run stays cut through what is written to it afterwards (a Keep in its review).</summary>
    [Fact]
    public async Task ACutIsTheRunsOwn_TheNextRunEndsLikeAnyOther()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var history = h.Server.CurrentSession!.Tools.History;
        var file = Path.Combine(h.RootDir, "A.cs");
        await File.WriteAllTextAsync(file, "a0\n");

        var first = history.BeginRun();
        await history.BackUpBeforeChangeAsync(file, CancellationToken.None);
        await File.WriteAllTextAsync(file, "a0\na1\n");
        history.CutCurrentRun();
        history.EndRun();
        history.CutCurrentRun();   // between two runs: nothing to cut

        var second = history.BeginRun();
        await history.BackUpBeforeChangeAsync(file, CancellationToken.None);
        await File.WriteAllTextAsync(file, "a0\na1\na2\n");
        history.EndRun();

        var runs = new FileHistoryService { WorkspaceRoot = () => h.RootDir, UserHistoryRoot = history.UserHistoryRoot }.Runs;
        Assert.True(runs.Single(r => r.Id == first).CutShort);
        Assert.False(runs.Single(r => r.Id == second).CutShort);
        Assert.False(runs.Single(r => r.Id == second).OpenElsewhere);
    }
}
