using System.IO;
using System.Linq;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The time budget of a chat turn is one decision for both front-ends: quick for a code action sent through the chat,
/// normal for a question — with tools or without.
/// </summary>
/// <remarks>
/// Each front-end chose on its own. Visual Studio gave a question asked with tools off the quick budget, so a slow or
/// cold model timed out at 120 s where VS Code waited 300 s for the same question; the host gave /explain and /review
/// the normal budget, where the settings call explain a quick task.
/// </remarks>
public sealed class ChatTurnBudgetParityTests
{
    [Fact]
    public void ACodeAction_IsQuick_AQuestion_IsNormal()
    {
        Assert.Equal(TaskComplexity.Quick,  ChatTurnPolicy.TurnComplexity(codeAction: true));
        Assert.Equal(TaskComplexity.Normal, ChatTurnPolicy.TurnComplexity(codeAction: false));
    }

    /// <summary>The model calls of a file's chat turn, read from its syntax tree (comments never count).</summary>
    private static InvocationExpressionSyntax[] ModelCalls(string relativePath)
    {
        var path = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), relativePath);
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot();
        return root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                   .Where(i => i.Expression is MemberAccessExpressionSyntax m
                            && m.Name.Identifier.Text is "RunAgentAsync" or "SendChatAsync")
                   .ToArray();
    }

    [Theory]
    [InlineData("Inferpal/ToolWindow/InferpalToolWindowData.ChatTurn.cs", 1)]
    [InlineData("Inferpal.Host/HostServer.cs", 2)]
    public void EveryChatTurnCall_TakesItsBudgetFromTheSharedDecision(string file, int atLeast)
    {
        var calls = ModelCalls(file);
        // WITNESS: the turn's model calls are really read — a renamed method would leave nothing to judge, green.
        Assert.True(calls.Length >= atLeast, $"{file}: only {calls.Length} model call(s) read.");

        foreach (var call in calls)
        {
            var text = call.ArgumentList.ToString();
            Assert.True(text.Contains("TurnComplexity", StringComparison.Ordinal) || text.Contains("loopComplexity", StringComparison.Ordinal),
                        $"{file}: a chat turn's model call chooses its own budget (or takes the default): {call.ToString()[..Math.Min(120, call.ToString().Length)]}");
            Assert.DoesNotContain("TaskComplexity.", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheVisualStudioLoopBudget_IsTheSharedDecision()
    {
        var path = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.ChatTurn.cs");
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot();
        var decl = root.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single(v => v.Identifier.Text == "loopComplexity");

        Assert.Equal("ChatTurnPolicy.TurnComplexity(codeAction: oneTimeModel is not null)", decl.Initializer!.Value.ToString());
    }
}
