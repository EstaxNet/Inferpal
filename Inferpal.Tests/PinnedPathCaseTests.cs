using System.IO;
using Inferpal.Services;
using Inferpal.Services.Prompting;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ A pinned-files edit asks TWO questions, and they do not fold case the same way.
/// "Did the editor change this line?" is a question about TEXT — a case correction is a change, on
/// every platform. "Is this the same file?" is a question about the FILE SYSTEM — the one answer is
/// <see cref="PathComparer"/>. Answering both with <c>OrdinalIgnoreCase</c> discarded the case
/// correction of a pin — under Linux, precisely the fix for a pin reported "not found".
/// </summary>
public class PinnedPathCaseTests
{
    [Fact]
    public void CorrectingTheCaseOfAPin_InTheSettings_IsKept()
    {
        // Pinned by hand as readme.md; the file is README.md. On a volume that does not fold case
        // the pin is "not found" — and the settings edit that corrects it must not be thrown away.
        var merged = PinnedFilesPolicy.MergeEdits(
            live: "/p/readme.md", opened: "/p/readme.md", edited: "/p/README.md");

        Assert.Equal("/p/README.md", merged);
    }

    [Fact]
    public void AnUnchangedLine_IsNotAnEdit()
    {
        // Reference arm: text equality still means "the editor left it alone" — a pin made from the
        // chat after the window opened survives an edit elsewhere.
        var merged = PinnedFilesPolicy.MergeEdits(
            live: "/p/a.md\n/p/b.md", opened: "/p/a.md", edited: "/p/a.md\n/p/c.md");

        Assert.Equal("/p/a.md\n/p/b.md\n/p/c.md", merged);
    }

    [Fact]
    public void AnAddedLine_NamingAPinnedFile_IsDeduplicatedByTheFileSystemsRule()
    {
        // The chat pinned /p/X.md after the window opened; the window adds /p/x.md. One file where
        // the volume folds case, two where it does not — the shared rule decides, not this site.
        var merged = PinnedFilesPolicy.MergeEdits(
            live: "/p/a.md\n/p/X.md", opened: "/p/a.md", edited: "/p/a.md\n/p/x.md");

        var sameFile = PathComparer.Default.Equals("/p/X.md", "/p/x.md");
        Assert.Equal(sameFile ? 2 : 3, merged.Split('\n').Length);
    }

    [Fact]
    public void PinningAFile_ThatDiffersOnlyByCase_IsDecidedByTheFileSystemsRule()
    {
        var decision = PinnedFilesPolicy.Decide(["/p/README.md"], "/p/readme.md");

        var sameFile = PathComparer.Default.Equals("/p/README.md", "/p/readme.md");
        Assert.Equal(sameFile ? PinDecision.Duplicate : PinDecision.Pin, decision);
    }

    /// <summary>
    /// The identity sites that run in the VS Code host, where Linux is a shipped platform: named
    /// assertions, because flipping the answer turns nothing red on a Windows workstation.
    /// </summary>
    [Theory]
    [InlineData("Inferpal.Core/Services/Prompting/PinnedFilesPolicy.cs", "current.Any(p => string.Equals(p, path")]
    [InlineData("Inferpal.Core/Services/Prompting/PinnedFilesPolicy.cs", ".Where(l => !chips.Contains(l")]
    [InlineData("Inferpal.Core/Services/Prompting/PinnedFilesPolicy.cs", "if (!result.Contains(line")]
    [InlineData("Inferpal.Host/HostServer.cs", "var kept    = current.Where(")]
    [InlineData("Inferpal.Host/HostServer.cs", "var attached = new HashSet<string>(")]
    [InlineData("Inferpal/ToolWindow/InferpalToolWindowData.Attachments.cs", ".ToHashSet(")]
    public void APinnedOrAttachedPathIdentity_IsDecidedByTheSharedRule(string file, string site)
    {
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(), file));

        // WITNESS: the comparison this case targets still exists, in this shape.
        var at = code.IndexOf(site, StringComparison.Ordinal);
        Assert.True(at >= 0, $"\"{site}\" is nowhere to be found in {file}: this case would have measured nothing.");

        var end  = code.IndexOf('\n', at);
        var line = code[at..(end > at ? end : code.Length)];
        Assert.Contains("PathComparer.", line, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
