using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio: Regenerate resends what the question was sent WITH — its one-time model and its attachments.
/// </summary>
/// <remarks>
/// Regenerate resent the bubble's words with no model and no attachment: a <c>/explain</c>, a <c>/review</c> or a
/// context-menu action lost the code it was about and came back as an ordinary agent turn — agent model, tools,
/// approvals —, and a question lost the file attached to it. VS Code replays the action itself. Read on the syntax tree:
/// the view-model needs a live Visual Studio.
/// </remarks>
public sealed class RegenerateResendsWhatWasSentTests
{
    private static MethodDeclarationSyntax Method(string file, string name)
    {
        var path = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal", "ToolWindow", file);
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().SingleOrDefault(m => m.Identifier.Text == name);
        Assert.True(method is not null, $"{name} was not found in {file}: this test would have measured nothing.");
        return method!;
    }

    [Fact]
    public void TheTurnRecordsWhatItSentWithItsQuestion()
    {
        var send = Method("InferpalToolWindowData.ChatTurn.cs", "SendCoreAsync");

        Assert.Contains(send.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
                        a => a.Left.ToString() == "_lastSent");
    }

    [Fact]
    public void Regenerate_ResendsTheRecordedModelAndAttachments_NotNothing()
    {
        var regenerate = Method("InferpalToolWindowData.ToolInvocation.cs", "RegenerateAsync");
        var resend = regenerate.DescendantNodes().OfType<InvocationExpressionSyntax>()
                               .Single(i => i.Expression.ToString() == "SendCoreAsync");
        var args = resend.ArgumentList.Arguments.Take(3).Select(a => a.Expression).ToList();

        Assert.DoesNotContain(args, e => e is LiteralExpressionSyntax l && l.IsKind(SyntaxKind.NullLiteralExpression));
        Assert.DoesNotContain(args, e => e is CollectionExpressionSyntax { Elements.Count: 0 });
        Assert.Contains(regenerate.DescendantNodes().OfType<IdentifierNameSyntax>(), n => n.Identifier.Text == "_lastSent");
    }
}
