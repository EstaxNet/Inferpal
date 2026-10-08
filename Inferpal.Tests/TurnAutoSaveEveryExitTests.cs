using System.IO;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A chat turn is auto-saved on EVERY way out of it — success, Stop, error — in both front-ends.
/// </summary>
/// <remarks>
/// The editor restores the auto-saved session when the chat opens. Visual Studio saved only at the end of a turn that
/// completed: a run the user stopped — its question, its partial answer, the steps of a run that changed files — or one
/// that failed was missing after a restart, while VS Code, which saves in its <c>finally</c>, kept it. Read on the
/// syntax tree: the window's view-model needs a live Visual Studio to run.
/// </remarks>
public sealed class TurnAutoSaveEveryExitTests
{
    [Fact]
    public void VisualStudio_SavesTheTurnInItsFinally()
    {
        var path = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal", "ToolWindow",
                                "InferpalToolWindowData.ChatTurn.cs");
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot();
        var send = root.DescendantNodes().OfType<MethodDeclarationSyntax>().SingleOrDefault(m => m.Identifier.Text == "SendCoreAsync");
        Assert.True(send is not null, "SendCoreAsync was not found: this test would have measured nothing.");

        // The turn's own try: the one that tells a Stop from an error.
        var turn = send!.DescendantNodes().OfType<TryStatementSyntax>()
            .SingleOrDefault(t => t.Catches.Count >= 2 && t.Finally is not null
                                  && t.Catches.Any(c => c.Declaration?.Type.ToString() == "OperationCanceledException"));
        Assert.True(turn is not null, "the turn's try (Stop, error, finally) was not found: check what replaced it.");

        static bool Saves(Microsoft.CodeAnalysis.SyntaxNode node) => node.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Any(i => i.Expression.ToString() == "AutoSaveAsync");

        Assert.True(Saves(turn!.Finally!.Block), "the turn is not auto-saved in its finally: a stopped or failed turn is lost.");
        Assert.False(Saves(turn.Block), "the turn is saved twice when it completes (once in the try, once in the finally).");
    }

    [Fact]
    public void VsCode_SavesTheTurnInItsFinally()
    {
        var path = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "vscode", "src", "chatViewProvider.ts");
        var body = ConversationPersistenceSilenceTests.TsMethodBody(path, "private async chatTurn(");

        var final = body.LastIndexOf("} finally {", StringComparison.Ordinal);
        Assert.True(final > 0, "chatTurn has no finally any more: check what replaced it.");
        Assert.Contains("this.autoSaveLast()", body[final..], StringComparison.Ordinal);
    }
}
