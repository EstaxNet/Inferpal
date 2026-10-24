using System.IO;
using Inferpal.Host;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Execution;
using StreamJsonRpc;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code's review card goes through the host: <c>run/review</c> draws it, <c>run/file</c> undoes, redoes or keeps one
/// file — never while a turn holds the host, whose run may still be writing that file.
/// </summary>
public partial class HostServerTests
{
    /// <summary>A closed run of the workspace that edited A.cs, kept on disk where the host reads its runs.</summary>
    private static async Task<(string RunId, string File)> ARunOnDiskAsync(string root)
    {
        var file = Path.Combine(root, "A.cs");
        await File.WriteAllTextAsync(file, "before");
        var history = new FileHistoryService { WorkspaceRoot = () => root };
        history.BeginRun();
        Assert.True((await history.BackUpBeforeChangeAsync(file, CancellationToken.None)).Item1);
        await File.WriteAllTextAsync(file, "by the run");
        history.EndRun();
        return (Assert.Single(history.Runs).Id, file);
    }

    [Fact]
    public async Task RunReview_DrawsTheRunsFiles_AndRunFile_UndoesOne()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var (runId, file) = await ARunOnDiskAsync(h.RootDir);

        var review = await h.Client.InvokeWithParameterObjectAsync<RunReviewDto>("run/review", new { runId });
        var undone = await h.Client.InvokeWithParameterObjectAsync<RunFileResult>("run/file", new { runId, path = file, action = "undo" });

        Assert.Equal("A.cs", Assert.Single(review.Files).Name);
        Assert.Equal("pending", review.Files[0].State);
        Assert.Equal(Strings.RunFileRestored("A.cs"), undone.Message);
        Assert.Equal("undone", Assert.Single(undone.Review!.Files).State);
        Assert.True(undone.Review.Files[0].CanRedo);
        Assert.Equal("before", await File.ReadAllTextAsync(file));
    }

    /// <summary>An action the host does not know is refused: read as an undo, a misspelt "keep" would overwrite the file.</summary>
    [Fact]
    public async Task RunFile_WithAnActionItDoesNotKnow_IsRefused_AndTheFileStays()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var (runId, file) = await ARunOnDiskAsync(h.RootDir);

        await Assert.ThrowsAsync<RemoteInvocationException>(() =>
            h.Client.InvokeWithParameterObjectAsync<RunFileResult>("run/file", new { runId, path = file, action = "Keep" }));

        Assert.Equal("by the run", await File.ReadAllTextAsync(file));
    }

    /// <summary>
    /// The card disables its actions on a click: each way out of <c>runFile</c> — done, refused, failed, no host — answers
    /// it, and an answer without a card gives the actions back. Otherwise one refusal left the whole card dead.
    /// </summary>
    [Fact]
    public void VsCode_EveryWayOutOfAFileAction_AnswersTheCard()
    {
        var provider = WebviewRebuildTests.TsCode("chatViewProvider.ts");
        var start = provider.IndexOf("case 'runFile': {", StringComparison.Ordinal);
        var body = provider[start..provider.IndexOf("case 'copyText':", start, StringComparison.Ordinal)];
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(body, @"this\.post\(\{ type: 'runReview', runId: msg\.runId").Count);
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(body, @"\breturn;|\} catch").Count);   // WITNESS: four ways out

        var webview = WebviewRebuildTests.TsCode("webview/main.ts");
        Assert.Contains("if (review === undefined) {", webview, StringComparison.Ordinal);
        Assert.Contains("forEach((b) => (b.disabled = false))", webview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunFile_WhileATurnRuns_IsRefused_AndTheFileStays()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var (runId, file) = await ARunOnDiskAsync(h.RootDir);
        var answering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Fake.OnChat = async (_, _) =>
        {
            answering.TrySetResult();
            await release.Task;
            return new ChatTurnResult("done", null, 0, 0);
        };
        var turn = h.Client.InvokeWithParameterObjectAsync<ChatSendResult>("chat/send", new { prompt = "hello", agentMode = false });
        await answering.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var refused = await Assert.ThrowsAsync<RemoteInvocationException>(() =>
            h.Client.InvokeWithParameterObjectAsync<RunFileResult>("run/file", new { runId, path = file, action = "undo" }));
        release.SetResult();
        await turn.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Equal(Strings.HostTurnBusy, refused.Message);
        Assert.Equal("by the run", await File.ReadAllTextAsync(file));
    }
}
