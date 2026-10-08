using System.IO;
using Inferpal.Host;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What the durable history keeps of a turn that wrote no text is decided once, for both front-ends.
/// </summary>
/// <remarks>
/// Tools called without a word: Visual Studio kept the "✓ Done — write_file ×2" line, the VS Code host kept nothing —
/// the next question then followed the previous one with no answer between them, the clients merge consecutive user
/// messages, and the model read both tasks as one request. An empty response: the host kept nothing, Visual Studio kept
/// the "empty response from model X" diagnostic as what the model had said.
/// </remarks>
[Collection(CultureSerialCollection.Name)]   // compares localized lines
public class PersistedAnswerParityTests
{
    private static readonly List<ToolExecution> Wrote = [new("write_file", "{}", "OK")];

    [Fact]
    public void ATurnThatOnlyCalledTools_KeepsItsToolSummary_AndAnEmptyResponseKeepsNothing()
    {
        Assert.Equal(ChatTurnPolicy.ToolSummaryAnswer(Wrote, null),
                     ChatTurnPolicy.PersistedAnswer(FinalAnswerKind.ToolSummary, null, string.Empty, Wrote, null));
        Assert.Equal(string.Empty,
                     ChatTurnPolicy.PersistedAnswer(FinalAnswerKind.EmptyFallback,
                                                    Strings.MsgEmptyResponseFrom("m", "http://x"), string.Empty, [], null));
        // Reference arm: an answer is the answer, without its reasoning.
        Assert.Equal("the answer",
                     ChatTurnPolicy.PersistedAnswer(FinalAnswerKind.StreamedAnswer, "<think>x</think>the answer", null, Wrote, null));
    }

    [Fact]
    public void VisualStudio_KeepsWhatThePolicyDecides()
    {
        var path = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal", "ToolWindow",
                                "InferpalToolWindowData.ChatTurn.cs");
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot();
        var send = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "SendCoreAsync");
        var calls = send.DescendantNodes().OfType<InvocationExpressionSyntax>().Select(i => i.Expression.ToString()).ToList();

        Assert.Contains(calls, c => c.EndsWith("ChatTurnPolicy.PersistedAnswer", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, c => c.EndsWith("ChatTurnPolicy.ChoosePersistedAnswer", StringComparison.Ordinal));
    }
}

public partial class HostServerTests
{
    [Fact]
    public async Task ChatSend_AToolTurnWithoutText_KeepsItsToolSummary_SoTheNextQuestionIsNotMergedIntoIt()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();
        var session = h.Server.CurrentSession!;
        session.ToolsEnabled = true;
        h.Fake.AgentRunResult = new AgentResult(string.Empty, [new ToolExecution("write_file", "{}", "OK")], []);

        await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
            "chat/send", new { prompt = "create notes.txt", agentMode = false })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var last = session.History[^1];
        Assert.Equal("assistant", last.Role);
        Assert.Contains("write_file", last.Content, StringComparison.Ordinal);
        Assert.Equal("user", session.History[^2].Role);                       // witness: the question before it
    }

    [Fact]
    public async Task ChatSend_AnEmptyResponse_KeepsNoAnswer()
    {
        // Reference arm: nothing was said, nothing is kept — the diagnostic the user reads is not the model's answer.
        using var h = CreateHarness();
        await h.InitializeAsync();
        var session = h.Server.CurrentSession!;
        session.ToolsEnabled = true;
        h.Fake.AgentRunResult = new AgentResult(string.Empty, [], []);

        await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
            "chat/send", new { prompt = "hello?", agentMode = false })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Equal("user", session.History[^1].Role);
    }
}
