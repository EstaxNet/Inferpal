using System.IO;
using Inferpal.Services.Governance;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// "Does this relative path leave the root?" has one answer for every message that names a path: <c>..</c> or <c>../x</c>
/// leave it, another drive leaves it — a file of the root whose name begins with two dots does not.
/// </summary>
public class PathLeavesRootTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "inferpal-repo");

    [Theory]
    [InlineData("..", true)]
    [InlineData("../x", true)]
    [InlineData(@"..\x", true)]
    [InlineData("..env.local", false)]
    [InlineData("..hidden/a.txt", false)]
    [InlineData("src/a.txt", false)]
    public void ARelativePath_LeavesTheRoot_OnlyByAParentStep(string relative, bool leaves)
    {
        Assert.Equal(leaves, PathSanitizer.LeavesRoot(relative));
    }

    [Fact]
    public void AFileWhoseNameBeginsWithTwoDots_IsShownRelative()
    {
        Assert.Equal("..env.local", ContentExclusions.Shown(Path.Combine(Root, "..env.local"), Root));
        Assert.Equal(Path.Combine(Path.GetTempPath(), "other.txt"), ContentExclusions.Shown(Path.Combine(Path.GetTempPath(), "other.txt"), Root));
    }
}
