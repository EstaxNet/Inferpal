using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Inferpal.Host.Acp;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>/run-copy</c> in an ACP client, which draws no card (pari 106): the start card asked as a permission request, the
/// question run in the copy under its command, the run's changes shown as diffs and applied together on the person's
/// answer — only then their file changes —, and <c>/run-copy</c> alone bringing the copy back to discard it.
/// </summary>
public sealed partial class AcpAgentTests
{
    private bool CommittedRoot()
    {
        File.WriteAllText(Path.Combine(_root, "Pay.cs"), "class Pay\n{\n    int A() => 1;\n}\n");
        foreach (var args in new[] { new[] { "init", "-q", "-b", "main", "." }, ["add", "Pay.cs"], ["commit", "-q", "-m", "start"] })
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-c", "user.email=test@example.com", "-c", "user.name=Test", "-c", "commit.gpgsign=false" }.Concat(args))
                psi.ArgumentList.Add(a);
            using var git = Process.Start(psi)!;
            git.StandardOutput.ReadToEnd();
            git.StandardError.ReadToEnd();
            if (!git.WaitForExit(30_000) || git.ExitCode != 0) return false;
        }
        return true;
    }

    private static string ToolCallId(JsonElement request) =>
        request.GetProperty("params").GetProperty("toolCall").GetProperty("toolCallId").GetString()!;

    /// <summary>The start card is the one that offers "copy"; the review, every other.</summary>
    private static bool IsStartCard(JsonElement permissionParams) =>
        permissionParams.GetProperty("options").EnumerateArray().Any(o => o.GetProperty("optionId").GetString() == "copy");

    private static IEnumerable<JsonElement> Permissions(Harness h) =>
        h.Client.Requests.Where(r => r.GetProperty("method").GetString() == "session/request_permission");

    [Fact]
    public async Task RunCopy_AsksItsStartCard_RunsInTheCopy_AndOnlyTheAnswerToItsReviewWritesThePersonsFile()
    {
        Assert.True(CommittedRoot(), "git is needed: this test is UNDECIDED, not green.");
        var pay = Path.Combine(_root, "Pay.cs");
        string? ranIn = null;
        var personsFileDuringTheRun = "";
        await using var h = await StartAsync(fake: f => f.OnChatRequest = (_, _, _, _) =>
        {
            ranIn = RunRoot.Copy;
            File.WriteAllText(Path.Combine(RunRoot.Copy!, "Pay.cs"), "class Pay\n{\n    int A() => 2;\n}\n");
            personsFileDuringTheRun = File.ReadAllText(pay);
            return Task.FromResult(new ChatTurnResult("Changed A.", null, 0, 0));
        });
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);
        h.Client.Answer = (method, p) => method != "session/request_permission" ? null
            : new { outcome = new { outcome = "selected", optionId = IsStartCard(p) ? "copy" : "apply" } };

        Assert.Equal("end_turn", StopReason(await PromptAsync(h, sid, "/run-copy Make A return 2")));

        var asks = Permissions(h).ToList();
        Assert.Equal(2, asks.Count);
        Assert.True(IsStartCard(asks[0].GetProperty("params")));
        Assert.NotEqual(ToolCallId(asks[0]), ToolCallId(asks[1]));                                    // a card of its own each
        var diff = asks[1].GetProperty("params").GetProperty("toolCall").GetProperty("content").EnumerateArray()
                          .Single(c => c.GetProperty("type").GetString() == "diff");
        Assert.Contains("int A() => 2;", diff.GetProperty("newText").GetString());
        Assert.Contains("int A() => 1;", diff.GetProperty("oldText").GetString());                    // a change, not a new file
        Assert.Equal(pay, diff.GetProperty("path").GetString(), PathComparer.Default);               // the person's file, absolute
        // An auto-approving client picks the first "allow": the review's is "keep", which writes and loses nothing.
        Assert.Equal("keep", asks[1].GetProperty("params").GetProperty("options")[0].GetProperty("optionId").GetString());
        Assert.NotNull(ranIn);
        Assert.False(PathComparer.SameDirectory(ranIn, _root));                                       // the run worked in the copy
        Assert.Contains("int A() => 1;", personsFileDuringTheRun);                                    // the person's file untouched meanwhile
        Assert.Contains("int A() => 2;", File.ReadAllText(pay));                                      // applied on the answer

        // `/run-copy` alone: the copy again, discarded this time.
        h.Client.Answer = (method, _) => method == "session/request_permission"
            ? new { outcome = new { outcome = "selected", optionId = "discard" } } : null;
        Assert.Equal("end_turn", StopReason(await PromptAsync(h, sid, "/run-copy")));
        Assert.False(Directory.Exists(ranIn));
    }

    [Fact]
    public async Task RunCopy_ATurnThatFails_StillAsksWhatToDoWithWhatItLeftInTheCopy()
    {
        Assert.True(CommittedRoot(), "git is needed: this test is UNDECIDED, not green.");
        var pay = Path.Combine(_root, "Pay.cs");
        string? copy = null;
        await using var h = await StartAsync(fake: f => f.OnChatRequest = (_, _, _, _) =>
        {
            copy = RunRoot.Copy;
            File.WriteAllText(Path.Combine(RunRoot.Copy!, "Pay.cs"), "class Pay\n{\n    int A() => 2;\n}\n");
            throw new HttpRequestException("the backend went away");
        });
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);
        h.Client.Answer = (method, p) => method != "session/request_permission" ? null
            : new { outcome = new { outcome = "selected", optionId = IsStartCard(p) ? "copy" : "keep" } };

        var response = await PromptAsync(h, sid, "/run-copy Make A return 2");

        Assert.True(response.TryGetProperty("error", out _));                                         // WITNESS: the turn failed
        var review = Permissions(h).Last().GetProperty("params");
        Assert.False(IsStartCard(review));
        Assert.Contains(review.GetProperty("toolCall").GetProperty("content").EnumerateArray(),
                        c => c.GetProperty("type").GetString() == "diff");
        Assert.Contains("int A() => 1;", File.ReadAllText(pay));                                      // kept: nothing written
        Assert.True(Directory.Exists(copy));                                                          // and the copy with it
    }

    [Fact]
    public async Task RunCopy_AReviewLeftUnanswered_WritesNothing_AndKeepsTheCopy()
    {
        Assert.True(CommittedRoot(), "git is needed: this test is UNDECIDED, not green.");
        var pay = Path.Combine(_root, "Pay.cs");
        string? copy = null;
        await using var h = await StartAsync(fake: f => f.OnChatRequest = (_, _, _, _) =>
        {
            copy = RunRoot.Copy;
            File.WriteAllText(Path.Combine(RunRoot.Copy!, "Pay.cs"), "class Pay\n{\n    int A() => 2;\n}\n");
            return Task.FromResult(new ChatTurnResult("Changed A.", null, 0, 0));
        });
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);
        h.Client.Answer = (method, p) => method != "session/request_permission" ? null
            : IsStartCard(p) ? new { outcome = (object)new { outcome = "selected", optionId = "copy" } }
            : new { outcome = (object)new { outcome = "cancelled" } };

        Assert.Equal("end_turn", StopReason(await PromptAsync(h, sid, "/run-copy Make A return 2")));

        Assert.Equal(2, Permissions(h).Count());                                                      // WITNESS: the review was asked
        Assert.Contains("int A() => 1;", File.ReadAllText(pay));
        Assert.True(Directory.Exists(copy));
    }

    /// <summary>A thread holding a copy is never closed to make room: reopened, it could not reach the copy any more (its
    /// record is owned by this live process), and the run's work would be out of reach until the agent exits.</summary>
    [Fact]
    public async Task RunCopy_AThreadThatHoldsACopy_IsNotClosedToMakeRoom()
    {
        Assert.True(CommittedRoot(), "git is needed: this test is UNDECIDED, not green.");
        await using var h = await StartAsync(fake: f => f.OnChatRequest = (_, _, _, _) =>
        {
            if (RunRoot.Copy is { } inCopy) File.WriteAllText(Path.Combine(inCopy, "Pay.cs"), "class Pay\n{\n    int A() => 2;\n}\n");
            return Task.FromResult(new ChatTurnResult("Changed A.", null, 0, 0));
        });
        await h.Client.RequestAsync("initialize", Init());
        var holding = await NewSessionAsync(h);
        h.Client.Answer = (method, p) => method != "session/request_permission" ? null
            : IsStartCard(p) ? new { outcome = (object)new { outcome = "selected", optionId = "copy" } }
            : new { outcome = (object)new { outcome = "cancelled" } };
        Assert.Equal("end_turn", StopReason(await PromptAsync(h, holding, "/run-copy Make A return 2")));
        var idle = await NewSessionAsync(h);
        Assert.Equal("end_turn", StopReason(await PromptAsync(h, idle, "hello")));

        for (var i = 0; i < AcpAgent.MaxLiveSessions - 1; i++) await NewSessionAsync(h);   // one past the cap
        Assert.True(h.Agent.IsLive(holding), "the thread holding a copy was closed to make room");
        Assert.False(h.Agent.IsLive(idle));   // WITNESS: a thread was closed — the idle one
    }

    /// <summary>A client owes no answer to a request sent after it cancelled: asked, the review would hold the prompt.</summary>
    [Fact]
    public async Task RunCopy_StoppedDuringTheRun_AsksNoReview_AndKeepsTheCopy()
    {
        Assert.True(CommittedRoot(), "git is needed: this test is UNDECIDED, not green.");
        var pay = Path.Combine(_root, "Pay.cs");
        string? copy = null;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = await StartAsync(fake: f => f.OnChat = async (_, ct) =>
        {
            copy = RunRoot.Copy;
            File.WriteAllText(Path.Combine(RunRoot.Copy!, "Pay.cs"), "class Pay\n{\n    int A() => 2;\n}\n");
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new ChatTurnResult(string.Empty, null, 0, 0);
        });
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);
        h.Client.Answer = (method, _) => method == "session/request_permission"
            ? new { outcome = new { outcome = "selected", optionId = "copy" } } : null;

        var prompt = PromptAsync(h, sid, "/run-copy Make A return 2");
        await entered.Task.WaitAsync(Wait);
        await h.Client.NotifyAsync("session/cancel", new { sessionId = sid });

        Assert.Equal("cancelled", StopReason(await prompt.WaitAsync(Wait)));
        Assert.Single(Permissions(h));                                                                // the start card only
        Assert.Contains("int A() => 1;", File.ReadAllText(pay));
        Assert.True(Directory.Exists(copy));
    }

    [Fact]
    public async Task RunCopy_WorkInMyFiles_RunsTheQuestionThere_WithoutACopy()
    {
        Assert.True(CommittedRoot(), "git is needed: this test is UNDECIDED, not green.");
        var ranInACopy = true;
        await using var h = await StartAsync(fake: f => f.OnChatRequest = (_, _, _, _) =>
        {
            ranInACopy = RunRoot.Copy is not null;
            return Task.FromResult(new ChatTurnResult("Done.", null, 0, 0));
        });
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);
        h.Client.Answer = (method, _) => method == "session/request_permission"
            ? new { outcome = new { outcome = "selected", optionId = "mine" } } : null;

        Assert.Equal("end_turn", StopReason(await PromptAsync(h, sid, "/run-copy Make A return 2")));

        Assert.False(ranInACopy);
        Assert.Single(Permissions(h));                                                                // no review: no copy
        Assert.Empty(await WorkCopy.FindAsync(_root, CancellationToken.None));
    }

    [Fact]
    public async Task RunCopy_StartCardCancelled_RunsNothing_AndMakesNoCopy()
    {
        Assert.True(CommittedRoot(), "git is needed: this test is UNDECIDED, not green.");
        var runs = 0;
        await using var h = await StartAsync(fake: f => f.OnChatRequest = (_, _, _, _) =>
        {
            runs++;
            return Task.FromResult(new ChatTurnResult("Changed.", null, 0, 0));
        });
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);
        h.Client.Answer = (method, _) => method == "session/request_permission"
            ? new { outcome = new { outcome = "selected", optionId = "cancel" } } : null;

        Assert.Equal("end_turn", StopReason(await PromptAsync(h, sid, "/run-copy Make A return 2")));

        Assert.Equal(0, runs);
        Assert.Empty(await WorkCopy.FindAsync(_root, CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(WorkCopy.Home, FileHistoryService.FolderNameOf(Path.GetFullPath(_root)))));
    }
}
