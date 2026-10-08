using System.IO;
using Inferpal.Services;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio: the workspace block describes the solution the root holds — a solution switch sends the new one.
/// </summary>
/// <remarks>
/// The block (solution, projects, open files) was sent once per conversation. Visual Studio keeps the conversation across
/// a solution switch, so the model went on working in solution B under A's projects and paths. One reader of "has the
/// root moved?" now serves the state that belongs to a root: this block and the session's "Always" approvals.
/// </remarks>
public sealed class WorkspaceBlockFollowsTheSolutionTests
{
    private static readonly string A = Path.Combine(Path.GetTempPath(), "inferpal-ws-a");

    [Fact]
    public void SameDirectory_ReadsAFolderWrittenAnotherWayAsTheSame()
    {
        Assert.True(PathComparer.SameDirectory(A, A + Path.DirectorySeparatorChar));
        Assert.True(PathComparer.SameDirectory(null, ""));
        Assert.False(PathComparer.SameDirectory(A, Path.Combine(Path.GetTempPath(), "inferpal-ws-b")));
        Assert.False(PathComparer.SameDirectory(A, null));
        Assert.Equal(!OperatingSystem.IsLinux(), PathComparer.SameDirectory(A, A.ToUpperInvariant()));
    }

    [Fact]
    public void TheBlockIsBuiltAgain_WhenTheRootItDescribedIsNoLongerTheRoot()
    {
        var path = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal", "ToolWindow",
                                "InferpalToolWindowData.ChatTurn.cs");
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot();
        var send = root.DescendantNodes().OfType<MethodDeclarationSyntax>().SingleOrDefault(m => m.Identifier.Text == "SendCoreAsync");
        Assert.True(send is not null, "SendCoreAsync was not found: this test would have measured nothing.");

        // The guard of the block's build reads the root it described…
        var guard = send!.DescendantNodes().OfType<IfStatementSyntax>()
                         .SingleOrDefault(i => i.Condition.ToString().Contains("_workspaceContextInjected", StringComparison.Ordinal));
        Assert.True(guard is not null, "the workspace block's guard was not found: check what replaced it.");
        Assert.Contains("SameDirectory(_workspaceContextRoot", guard!.Condition.ToString(), StringComparison.Ordinal);

        // …and a block that was built records that root.
        Assert.Contains(guard.Statement.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
                        a => a.Left.ToString() == "_workspaceContextRoot");
    }
}
