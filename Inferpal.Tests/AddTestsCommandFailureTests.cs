using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// "Add unit tests" (right-click) said nothing when generation failed — its siblings
/// Refactor/Fix/Document say so — and the cause never reached /diagnostics.
/// </summary>
public class AddTestsCommandFailureTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Commands(string file) =>
        ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(), "Inferpal", "Commands", file));

    [Fact]
    public void AFailedGeneration_FromTheContextMenu_SaysSo()
    {
        Assert.Contains("InPlaceEditOutcome.Failed", Commands("InPlaceCodeActionBase.cs"), StringComparison.Ordinal);

        var command = Commands("AddTestsSelectionCommand.cs");
        Assert.Contains("Strings.TestsNoChange", command, StringComparison.Ordinal);
        Assert.Contains("Strings.TestsGenerateFailed", command, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryFailureOfTheTestGeneration_IsTraced()
    {
        var path    = Path.Combine(RepoRoot(), "Inferpal", "Commands", "TestGenerationEdit.cs");
        var root    = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();
        var catches = root.DescendantNodes().OfType<CatchClauseSyntax>().ToList();
        Assert.True(catches.Count >= 3, $"only {catches.Count} catch clause(s) found: the rule reads nothing");

        var silent = catches
            .Where(c => c.Declaration?.Type.ToString() != "OperationCanceledException")
            .Where(c => !c.Block.ToString().Contains("Diagnostics.Swallow(", StringComparison.Ordinal))
            .Select(c => $"line {c.GetLocation().GetLineSpan().StartLinePosition.Line + 1}")
            .ToList();
        Assert.True(silent.Count == 0, "untraced catch in TestGenerationEdit: " + string.Join(", ", silent));
    }
}
