using System.IO;
using Inferpal.Host;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A turn the user STOPPED keeps, after its question, the answer the user saw — marked stopped. Left unanswered, the
/// question was answered first at the next one: the long answer just stopped, written again before the one asked.
/// </summary>
public class StoppedAnswerKeptTests
{
    [Fact]
    public void TheShownPart_IsKept_AndMarkedStopped()
    {
        var kept = ChatTurnPolicy.StoppedAnswer("<think>plan</think>The history of text editors begins");

        Assert.Equal("assistant", kept.Role);
        Assert.Equal("The history of text editors begins\n\n" + ChatTurnPolicy.StoppedMarker, kept.Content);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<think>still thinking")]
    public void NothingShown_KeepsTheMarkerAlone(string? shown) =>
        Assert.Equal(ChatTurnPolicy.StoppedMarker, ChatTurnPolicy.StoppedAnswer(shown).Content);

    [Fact]
    public void VisualStudio_KeepsTheStoppedAnswer_WhereItShowsCancelled()
    {
        var path = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal", "ToolWindow",
                                "InferpalToolWindowData.ChatTurn.cs");
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot();
        var send = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "SendCoreAsync");
        var stopCatches = send.DescendantNodes().OfType<CatchClauseSyntax>()
            .Where(c => c.Declaration?.Type.ToString() == "OperationCanceledException")
            .ToList();

        Assert.NotEmpty(stopCatches);                                                     // witness: the stop paths are read
        Assert.Contains(stopCatches, c => c.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Any(i => i.Expression.ToString().EndsWith("ChatTurnPolicy.StoppedAnswer", StringComparison.Ordinal)));
    }
}

public partial class HostServerTests
{
    private static async Task<(ChatSendResult Result, HostSession Session)> StopMidAnswerAsync(Harness h)
    {
        await h.InitializeAsync();
        var session = h.Server.CurrentSession!;
        session.ToolsEnabled = false;
        h.Fake.OnChat = async (onToken, ct) =>
        {
            onToken?.Invoke("The history of text editors begins");
            await Task.Delay(Timeout.Infinite, ct);   // hangs until chat/cancel
            return new ChatTurnResult(string.Empty, null, 0, 0);
        };

        var sendTask = h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
            "chat/send", new { prompt = "write a 600-word essay", agentMode = false });
        await h.Target.FirstToken.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        await h.Client.InvokeAsync("chat/cancel");
        return (await sendTask.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs)), session);
    }

    [Fact]
    public async Task ChatSend_StoppedMidAnswer_KeepsWhatWasShown_MarkedStopped()
    {
        using var h = CreateHarness();
        var (result, session) = await StopMidAnswerAsync(h);

        Assert.True(result.Cancelled);
        Assert.Equal("user", session.History[^2].Role);
        Assert.Contains("600-word essay", session.History[^2].Content, StringComparison.Ordinal);
        Assert.Equal("The history of text editors begins\n\n" + ChatTurnPolicy.StoppedMarker, session.History[^1].Content);
        Assert.Equal(AgentOrchestrator.EstimateTokens(session.History), session.LastPromptTokens);
    }

    [Fact]
    public async Task ChatSend_StoppedMidAnswer_ThenRegenerate_TakesTheQuestionAndItsStoppedAnswerBack()
    {
        using var h = CreateHarness();
        var (_, session) = await StopMidAnswerAsync(h);
        var before = session.History.Count;

        var removed = await h.Client.InvokeAsync<bool>("chat/rollbackLastTurn");

        Assert.True(removed);
        Assert.Equal(before - 2, session.History.Count);
        Assert.DoesNotContain(session.History, m => m.Content?.Contains(ChatTurnPolicy.StoppedMarker, StringComparison.Ordinal) == true);
    }
}
