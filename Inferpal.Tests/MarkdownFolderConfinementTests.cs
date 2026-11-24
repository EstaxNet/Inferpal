using System.IO;
using Inferpal.Services.Governance;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The rules, checks and prompt files of <c>.inferpal/</c> are files a cloned repository controls, read into the prompt:
/// read as every such file is — a link that leaves the repository is not followed, a file past the size cap not read, and
/// both are listed with the unreadable ones.
/// </summary>
public sealed class MarkdownFolderConfinementTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("inferpal-mdfolder-").FullName;
    private string Rules => Path.Combine(_repo, ".inferpal", "rules");

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch { /* teardown never throws (TeardownDoesNotThrowTests) */ }
    }

    [Fact]
    public void ARuleLinkedOutOfTheRepository_IsNotRead_AndIsListed()
    {
        Directory.CreateDirectory(Rules);
        File.WriteAllText(Path.Combine(Rules, "style.md"), "Use tabs.");
        var outside = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"outside-{Guid.NewGuid():N}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "OUTSIDE-SECRET");
        try
        {
            try { File.CreateSymbolicLink(Path.Combine(Rules, "leak.md"), outside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }   // no privilege: undecided

            var read = MarkdownFolder.ReadAll(Rules, "MarkdownFolderConfinementTests", out var unreadable);
            Assert.Equal(["Use tabs."], read.Select(r => r.Text));
            Assert.Equal(["leak.md"], unreadable);
        }
        finally { try { File.Delete(outside); } catch { } }
    }

    [Fact]
    public void ARulePastTheSizeCap_IsNotRead_AndIsListed()
    {
        Directory.CreateDirectory(Rules);
        File.WriteAllText(Path.Combine(Rules, "huge.md"), new string('x', Inferpal.Services.Prompting.RepoInstructionReader.MaxFileBytes + 1));
        var read = MarkdownFolder.ReadAll(Rules, "MarkdownFolderConfinementTests", out var unreadable);
        Assert.Empty(read);
        Assert.Equal(["huge.md"], unreadable);
    }
}
