using System.IO;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An ACP thread's saves run one at a time, each reading the transcript once it holds its turn.
/// </summary>
/// <remarks>
/// A turn's end and a <c>/task</c> report a moment later each started a save; both reached the host in parallel, and the
/// one that had read the older transcript could finish last — the report gone from the saved thread, and nothing saved
/// after it. Seen as <c>AcpAgentTests.ATasksReport_IsKeptWithTheThread</c> going red on the macOS runner, twice. The
/// order of two requests inside the host cannot be forced from here: the rule is read on the source.
/// </remarks>
public sealed class AcpSaveOrderTests
{
    [Fact]
    public void ASave_ReadsTheTranscript_OnlyOnceItHoldsTheSaveTurn()
    {
        var path = Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal.Host", "Acp", "AcpSession.cs");
        var save = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot()
                                   .DescendantNodes().OfType<MethodDeclarationSyntax>()
                                   .SingleOrDefault(m => m.Identifier.Text == "SaveAsync");
        Assert.True(save is not null, "AcpSession.SaveAsync moved: this test reads nothing.");   // witness

        var body = save!.Body!.ToString();
        var wait = body.IndexOf("_saving.WaitAsync()", StringComparison.Ordinal);
        var read = body.IndexOf("[.. _transcript]", StringComparison.Ordinal);
        Assert.True(wait >= 0 && read >= 0, "the save gate or the transcript read is gone: check what replaced them.");
        Assert.True(wait < read, "the transcript is read before the save holds its turn.");
        Assert.Contains("_saving.Release()", body, StringComparison.Ordinal);
    }
}
