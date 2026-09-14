using Inferpal.Services.Prompting;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The workspace block of a conversation's first question, shared by both front-ends.
/// </summary>
public class WorkspaceContextTests
{
    [Fact]
    public void NothingToSay_GivesNoBlock() =>
        Assert.Equal(string.Empty, WorkspaceContext.Compose(null, "   "));

    [Fact]
    public void ASolutionAlone_GivesTheSolutionSection_AndNoEmptyEditorsSection()
    {
        var block = WorkspaceContext.Compose("Solution : App.sln", null);

        Assert.StartsWith(WorkspaceContext.Header, block, StringComparison.Ordinal);
        Assert.Contains("### Solution\n\nSolution : App.sln", block, StringComparison.Ordinal);
        Assert.DoesNotContain("### Open editors", block, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenEditorsAlone_GiveTheEditorsSection_AndNoEmptySolutionSection()
    {
        var block = WorkspaceContext.Compose("", "Open files (1):\n    C:\\ws\\Program.cs");

        Assert.StartsWith(WorkspaceContext.Header, block, StringComparison.Ordinal);
        Assert.Contains("### Open editors\n\nOpen files (1):", block, StringComparison.Ordinal);
        Assert.DoesNotContain("### Solution", block, StringComparison.Ordinal);
    }

    [Fact]
    public void BothSections_KeepTheSolutionFirst()
    {
        var block = WorkspaceContext.Compose("Solution : App.sln", "Open files (1):");

        Assert.True(block.IndexOf("### Solution", StringComparison.Ordinal)
                    < block.IndexOf("### Open editors", StringComparison.Ordinal));
    }
}
