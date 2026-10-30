using System.IO;
using Inferpal.Host;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Persistence;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A question the turn ended without answering (a failed request) is closed by a marked non-answer, live and on reload.
/// Left last in the history, the next question landed right after it, every client merged the two <c>user</c> messages,
/// and the model answered the earlier one again.
/// </summary>
public class UnansweredQuestionTests
{
    [Fact]
    public void CloseUnanswered_AddsTheMarker_OnlyAfterAQuestion()
    {
        var asked = new List<ChatMessageDto> { new("system", "s"), new("user", "q") };
        var answered = new List<ChatMessageDto> { new("system", "s"), new("user", "q"), new("assistant", "a") };

        Assert.True(ChatTurnPolicy.CloseUnanswered(asked));
        Assert.Equal(ChatTurnPolicy.NoAnswerMarker, asked[^1].Content);
        Assert.Equal("assistant", asked[^1].Role);
        Assert.False(ChatTurnPolicy.CloseUnanswered(answered));
        Assert.Equal("a", answered[^1].Content);
    }

    /// <summary>/plan save writes the last ANSWER: after a turn that has none, there is nothing to save — never the plan
    /// of an earlier question in its place, and never the marker.</summary>
    [Fact]
    public void TheLastAnswer_IsNone_AfterATurnWithoutOne()
    {
        var plan = new List<ChatMessageDto> { new("user", "plan the migration"), new("assistant", "1. Move the types") };
        Assert.Equal("1. Move the types", ChatTurnPolicy.LastAnswer(plan));                  // reference arm

        Assert.Null(ChatTurnPolicy.LastAnswer([.. plan, new("user", "go"), ChatTurnPolicy.NoAnswer()]));
        Assert.Null(ChatTurnPolicy.LastAnswer([.. plan, new("user", "again"), ChatTurnPolicy.StoppedAnswer(null)]));
    }

    [Fact]
    public void ARestoredConversation_MarksEveryQuestionLeftWithoutAnAnswer()
    {
        var restored = SessionManager.BuildRestoredHistory("sys",
        [
            new SavedMessage("user", "first question"),
            new SavedMessage("user", "second question"),
            new SavedMessage("assistant", "the answer to the second"),
            new SavedMessage("user", "third question"),
        ]);

        // The last question stays as it is: the next question closes it, as live.
        Assert.Equal(["system", "user", "assistant", "user", "assistant", "user"], restored.Select(m => m.Role).ToArray());
        Assert.Equal(ChatTurnPolicy.NoAnswerMarker, restored[2].Content);
        Assert.Equal("the answer to the second", restored[4].Content);
    }

    [Fact]
    public void ARestoredConversation_AnsweredThroughout_GainsNothing()
    {
        var restored = SessionManager.BuildRestoredHistory("sys",
        [
            new SavedMessage("user", "q"),
            new SavedMessage("assistant", "a"),
        ]);

        Assert.DoesNotContain(restored, m => m.Content == ChatTurnPolicy.NoAnswerMarker);
    }

    /// <summary>The marker goes in on the way to the question, in the same block, before the system prompt is rebuilt
    /// (which must stay right before the question: <c>SystemPromptPerTurnTests</c>).</summary>
    [Fact]
    public void VisualStudio_ClosesAnUnansweredQuestion_BeforeTheNextOneJoinsTheHistory()
    {
        var path = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal", "ToolWindow",
                                "InferpalToolWindowData.ChatTurn.cs");
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot();
        var appends = root.DescendantNodes().OfType<ExpressionStatementSyntax>()
            .Where(s => s.Expression.ToString().StartsWith("_history.Add(new ChatMessageDto(\"user\"", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(appends);                                                         // witness: the append is read
        Assert.All(appends, append =>
        {
            var block = Assert.IsType<BlockSyntax>(append.Parent);
            var before = block.Statements.Take(block.Statements.IndexOf(append)).Select(s => s.ToString()).ToList();
            var closes = before.FindLastIndex(s => s.EndsWith("ChatTurnPolicy.CloseUnanswered(_history);", StringComparison.Ordinal));
            var rebuilds = before.FindLastIndex(s => s.StartsWith("RefreshSystemPrompt(", StringComparison.Ordinal));
            Assert.True(closes >= 0, "the question joins the history without closing an unanswered one");
            Assert.True(closes < rebuilds, "the marker must go in before the system prompt is rebuilt");
        });
    }
}

public partial class HostServerTests
{
    private static async Task<List<ChatMessageDto>> NextQuestionAfterAFailureAsync(bool tools)
    {
        using var h = CreateHarness();
        await h.InitializeAsync();
        h.Server.CurrentSession!.ToolsEnabled = tools;
        var calls = 0;
        List<ChatMessageDto>? second = null;
        h.Fake.OnChatRequest = (_, messages, _, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new AgentHttpException("the server restarted", isTimeout: false);
            second = [.. messages];
            return Task.FromResult(new ChatTurnResult("done", null, 0, 0));
        };

        var failed = await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
            "chat/send", new { prompt = "rename the class Foo", agentMode = false });
        await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
            "chat/send", new { prompt = "continue", agentMode = false });

        Assert.False(string.IsNullOrEmpty(failed.Error));                               // witness: the first turn failed
        return second!;
    }

    /// <summary>Regenerate after the failure: the question asked again leaves with its answer, and the failed one keeps
    /// its marker — the history alternates whatever is taken back.</summary>
    [Fact]
    public async Task ChatSend_RegenerateAfterAFailedTurn_KeepsTheHistoryAlternating()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();
        var session = h.Server.CurrentSession!;
        session.ToolsEnabled = false;
        var calls = 0;
        h.Fake.OnChatRequest = (_, _, _, _) => Interlocked.Increment(ref calls) == 1
            ? throw new AgentHttpException("the server restarted", isTimeout: false)
            : Task.FromResult(new ChatTurnResult("done", null, 0, 0));

        await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>("chat/send", new { prompt = "rename Foo", agentMode = false });
        await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>("chat/send", new { prompt = "continue", agentMode = false });
        Assert.True(await h.Client.InvokeAsync<bool>("chat/rollbackLastTurn"));

        Assert.Equal(ChatTurnPolicy.NoAnswerMarker, session.History[^1].Content);
        Assert.Contains("rename Foo", session.History[^2].Content, StringComparison.Ordinal);
        Assert.DoesNotContain(session.History, m => m.Content?.Contains("continue", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData(true)]    // the basic tool loop: a failed run
    [InlineData(false)]   // the plain chat: a request that threw
    public async Task ChatSend_AfterAFailedTurn_TheNextQuestionIsNotMergedIntoTheFailedOne(bool tools)
    {
        var sent = await NextQuestionAfterAFailureAsync(tools);

        var failed = sent.FindIndex(m => m.Role == "user" && m.Content!.Contains("rename the class Foo", StringComparison.Ordinal));
        Assert.True(failed > 0);
        Assert.Equal("assistant", sent[failed + 1].Role);
        Assert.Equal(ChatTurnPolicy.NoAnswerMarker, sent[failed + 1].Content);
        Assert.Equal("user", sent[failed + 2].Role);
        Assert.Contains("continue", sent[failed + 2].Content, StringComparison.Ordinal);
    }
}
