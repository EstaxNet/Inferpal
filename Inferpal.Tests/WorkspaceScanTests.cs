using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The single directory-skip policy that replaced seven private copies (review 2026-08-07).
/// </summary>
/// <remarks>
/// Each case below is a divergence one of those copies actually had, so this file doubles as the
/// record of what was wrong: they are regression pins, not a specification written afterwards.
/// </remarks>
public class WorkspaceScanTests
{
    [Theory]
    [InlineData(@"C:\p\obj\Debug\a.cs")]
    [InlineData(@"C:\p\bin\Release\a.cs")]
    [InlineData(@"C:\p\.git\config")]
    [InlineData(@"C:\p\.vs\x")]
    [InlineData(@"C:\p\node_modules\lib\a.js")]
    [InlineData(@"C:\p\packages\lib\a.cs")]
    [InlineData(@"C:\p\dist\a.js")]
    [InlineData(@"C:\p\build\a.o")]
    [InlineData(@"C:\p\.generated\a.cs")]
    [InlineData("/home/p/obj/a.cs")]
    [InlineData("/home/p/.git/config")]
    public void ExcludedDirectories_AreSkipped(string path) =>
        Assert.True(WorkspaceScan.IsExcludedPath(path));

    [Theory]
    [InlineData(@"C:\p\src\a.cs")]
    [InlineData(@"C:\p\Services\obj.cs")]        // a file named obj, not a directory
    [InlineData(@"C:\p\rebuilder\a.cs")]         // "build" as a substring of a real folder
    [InlineData("/home/p/src/a.cs")]
    public void OrdinarySources_AreKept(string path) =>
        Assert.False(WorkspaceScan.IsExcludedPath(path));

    [Theory]
    [InlineData(@"C:\p\Obj\Debug\a.cs")]
    [InlineData(@"C:\p\Node_Modules\lib\a.js")]
    [InlineData(@"C:\p\.VS\x")]
    public void CaseIsIgnored(string path)
    {
        // ⚠ rename_symbol — the tool that writes — compared with StringComparison.Ordinal, so these
        // were not excluded at all on a case-insensitive filesystem and it rewrote generated code.
        Assert.True(WorkspaceScan.IsExcludedPath(path));
    }

    [Fact]
    public void InferpalsOwnDirectoryIsExcluded()
    {
        // Only two of the seven copies knew this. `.inferpal/history/` holds COPIES of the user's
        // source files, same extensions: every walker that missed it was analysing stale
        // duplicates — and rename_symbol was editing them.
        Assert.True(WorkspaceScan.IsExcludedPath(@"C:\p\.inferpal\history\run1\Program.cs"));
    }

    [Fact]
    public void ForwardSlashGitIsExcluded()
    {
        // analyze_impact excluded \.git\ but not /.git/.
        Assert.True(WorkspaceScan.IsExcludedPath("/home/p/.git/objects/ab/cdef"));
    }

    [Theory]
    [InlineData(@"C:\p\node_modules", true)]
    [InlineData(@"C:\p\node_modules\", true)]
    [InlineData(@"C:\p\src", false)]
    public void DirectoryNamesAreJudgedOnTheirLeaf(string dir, bool skipped) =>
        Assert.Equal(skipped, WorkspaceScan.IsExcludedDirName(dir));
}
