using System.IO;
using Inferpal.Host;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Execution;
using StreamJsonRpc;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code goes back to a question through the host: <c>chat/rewindPlan</c> draws the card, <c>chat/rewind</c> undoes the
/// runs that followed it — all or none — cuts the conversation before it and hands the question back.
/// </summary>
public partial class HostServerTests
{
    /// <summary>Two questions; the second one's run edited A.cs (written to disk where the host reads its runs).</summary>
    private static async Task<(string File, List<SavedMessageDto> Messages)> AConversationWithARunAsync(string root)
    {
        var file = Path.Combine(root, "A.cs");
        await File.WriteAllTextAsync(file, "before");
        var history = new FileHistoryService { WorkspaceRoot = () => root };
        history.BeginRun();
        Assert.True((await history.BackUpBeforeChangeAsync(file, CancellationToken.None)).Item1);
        await File.WriteAllTextAsync(file, "by the run");
        history.EndRun();
        return (file, [
            new("user", "Explain A"), new("assistant", "A does this."),
            new("user", "Change A"), new("assistant", "Changed.", Run: Assert.Single(history.Runs).Id),
        ]);
    }

    [Fact]
    public async Task Rewind_UndoesTheRunsThatFollowed_CutsTheConversation_AndGivesTheQuestionBack()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var (file, messages) = await AConversationWithARunAsync(h.RootDir);

        var card   = await h.Client.InvokeWithParameterObjectAsync<RewindCardDto>("chat/rewindPlan", new { messageIndex = 2, messages });
        var result = await h.Client.InvokeWithParameterObjectAsync<RewindResult>("chat/rewind", new { messageIndex = 2, messages });

        Assert.False(card.Refused);
        Assert.Equal("A.cs", Assert.Single(card.Files).Name);
        Assert.Equal("before", await File.ReadAllTextAsync(file));
        Assert.Equal("Change A", result.Question);
        Assert.Equal(["Explain A", "A does this."], result.Branch!.Messages.Select(m => m.Content));
        Assert.Equal(Strings.RewindDone(2, 1, 0), result.Message);
    }

    [Fact]
    public async Task Rewind_RefusedForAFileChangedSince_ChangesNothing_AndOffersTheConversationOnly()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var (file, messages) = await AConversationWithARunAsync(h.RootDir);
        await File.WriteAllTextAsync(file, "edited by hand");

        var refused = await h.Client.InvokeWithParameterObjectAsync<RewindResult>("chat/rewind", new { messageIndex = 2, messages });
        var only    = await h.Client.InvokeWithParameterObjectAsync<RewindResult>("chat/rewind",
                          new { messageIndex = 2, messages, conversationOnly = true });

        Assert.True(refused.Card!.Refused);
        Assert.Null(refused.Branch);
        Assert.Equal("edited by hand", await File.ReadAllTextAsync(file));          // refused: untouched
        Assert.Equal(2, only.Branch!.Messages.Count);                              // the conversation only: cut…
        Assert.Equal("edited by hand", await File.ReadAllTextAsync(file));          // …and the file still as it is
        Assert.Equal(Strings.RewindDoneConversationOnly(2), only.Message);
    }

    [Fact]
    public async Task Rewind_WhileATurnRuns_IsRefused_AndTheFileStays()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var (file, messages) = await AConversationWithARunAsync(h.RootDir);
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
            h.Client.InvokeWithParameterObjectAsync<RewindResult>("chat/rewind", new { messageIndex = 2, messages }));
        release.SetResult();
        await turn.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Equal(Strings.HostTurnBusy, refused.Message);
        Assert.Equal("by the run", await File.ReadAllTextAsync(file));
    }

    /// <summary>The index points at another question than the one clicked (the view moved under the card): nothing done.</summary>
    [Fact]
    public async Task Rewind_ToAQuestionThatIsNoLongerThere_DoesNothing()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var (file, messages) = await AConversationWithARunAsync(h.RootDir);

        var result = await h.Client.InvokeWithParameterObjectAsync<RewindResult>("chat/rewind",
            new { messageIndex = 2, messages, question = "Explain A" });

        Assert.Equal(Strings.RewindQuestionGone, result.Message);
        Assert.Null(result.Branch);
        Assert.Equal("by the run", await File.ReadAllTextAsync(file));
    }

    /// <summary>Back to the first question: a new conversation — the whole one saved, no empty branch written for it.</summary>
    [Fact]
    public async Task Rewind_ToTheFirstQuestion_SavesTheParent_AndNoEmptySession()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var (_, messages) = await AConversationWithARunAsync(h.RootDir);

        var result   = await h.Client.InvokeWithParameterObjectAsync<RewindResult>("chat/rewind", new { messageIndex = 0, messages });
        var sessions = await h.Client.InvokeAsync<SessionListResult>("session/list");

        Assert.Empty(result.Branch!.Messages);
        Assert.Equal("Explain A", result.Question);
        // The parent, whole; no `__b2` with nothing in it (the suite's other sessions live in other folders).
        var mine = Assert.Single(sessions.Sessions, x => x.WorkspaceRoot == h.RootDir);
        Assert.Equal(4, mine.MessageCount);
    }

    /// <summary>A slash command's echo is not a question: nothing to go back to.</summary>
    [Fact]
    public async Task RewindPlan_OnSomethingThatIsNotAQuestion_IsNull()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var (_, messages) = await AConversationWithARunAsync(h.RootDir);

        var card = await h.Client.InvokeWithParameterObjectAsync<RewindCardDto?>("chat/rewindPlan", new { messageIndex = 1, messages });

        Assert.Null(card);
    }
}
