using System.IO;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// When writing an <c>apply_edits</c> lot fails and files could not be put back, the model is told which, named as every
/// message names a path (relative, forward slashes; whole outside the workspace), and what to do with them — "them" for
/// several, as <c>replace_in_files</c> says it.
/// </summary>
public class ApplyEditsWriteFailedTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "inferpal-shop");

    [Fact]
    public void SeveralFilesNotPutBack_AreNamedAsMessagesNamePaths_AndCalledThem()
    {
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere", "c.cs");
        var write = new SafeFileWriter.MultiWriteResult(Path.Combine(Root, "src", "a.cs"), "disk full",
                                                        [Path.Combine(Root, "src", "b.cs"), outside]);
        var text = ApplyEditsTool.WriteFailed(write, Root);
        Assert.Contains("writing src/a.cs failed (disk full), and src/b.cs, " + outside + " could not be put back — restore them", text);
    }

    [Fact]
    public void OneFileNotPutBack_IsCalledIt()
    {
        var write = new SafeFileWriter.MultiWriteResult(Path.Combine(Root, "a.cs"), "locked", [Path.Combine(Root, "b.cs")]);
        Assert.Contains("b.cs could not be put back — restore it with restore_file", ApplyEditsTool.WriteFailed(write, Root));
    }
}
