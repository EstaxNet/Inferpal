using System.IO;
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
    // The workspace these paths belong to: exclusions are judged below it, never above.
    private static string RootOf(string path) => path.StartsWith('/') ? "/home/p" : @"C:\p";

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
    // A virtual environment is never the user's code: a fresh one already holds ~400 .py files of
    // pip, and the VS Code host indexes the workspace when it opens.
    [InlineData(@"C:\p\.venv\Lib\site-packages\pip\__init__.py")]
    [InlineData("/home/p/venv/lib/python3.12/site-packages/pip/__init__.py")]
    [InlineData("/home/p/obj/a.cs")]
    [InlineData("/home/p/.git/config")]
    public void ExcludedDirectories_AreSkipped(string path) =>
        Assert.True(WorkspaceScan.IsExcludedPath(path, RootOf(path)));

    [Theory]
    [InlineData(@"C:\p\src\a.cs")]
    [InlineData(@"C:\p\Services\obj.cs")]        // a file named obj, not a directory
    [InlineData(@"C:\p\rebuilder\a.cs")]         // "build" as a substring of a real folder
    [InlineData("/home/p/src/a.cs")]
    public void OrdinarySources_AreKept(string path) =>
        Assert.False(WorkspaceScan.IsExcludedPath(path, RootOf(path)));

    [Theory]
    [InlineData(@"C:\p\Obj\Debug\a.cs")]
    [InlineData(@"C:\p\Node_Modules\lib\a.js")]
    [InlineData(@"C:\p\.VS\x")]
    public void CaseIsIgnored(string path)
    {
        // ⚠ rename_symbol — the tool that writes — compared with StringComparison.Ordinal, so these
        // were not excluded at all on a case-insensitive filesystem and it rewrote generated code.
        Assert.True(WorkspaceScan.IsExcludedPath(path, RootOf(path)));
    }

    [Fact]
    public void InferpalsOwnDirectoryIsExcluded()
    {
        // Only two of the seven copies knew this. `.inferpal/history/` holds COPIES of the user's
        // source files, same extensions: every walker that missed it was analysing stale
        // duplicates — and rename_symbol was editing them.
        Assert.True(WorkspaceScan.IsExcludedPath(@"C:\p\.inferpal\history\run1\Program.cs", @"C:\p"));
    }

    [Fact]
    public void ForwardSlashGitIsExcluded()
    {
        // analyze_impact excluded \.git\ but not /.git/.
        Assert.True(WorkspaceScan.IsExcludedPath("/home/p/.git/objects/ab/cdef", "/home/p"));
    }

    /// <summary>
    /// The folders ABOVE the workspace are not build output. Judged on the absolute path, a workspace
    /// kept under build/, dist/, bin/ or packages/ lost every file to list_files, search_in_files, the
    /// project map, the impact and rename tools and the semantic index.
    /// </summary>
    [Theory]
    [InlineData(@"C:\work\build\app\src\a.cs", @"C:\work\build\app")]
    [InlineData(@"D:\packages\lib\src\a.cs", @"D:\packages\lib\")]
    [InlineData("/home/u/dist/site/src/a.ts", "/home/u/dist/site")]
    public void TheFoldersAboveTheRoot_DoNotCount(string path, string root) =>
        Assert.False(WorkspaceScan.IsExcludedPath(path, root));

    /// <summary>Reference arm: below the same root, an excluded folder still is.</summary>
    [Fact]
    public void BelowTheRoot_ExclusionsStillApply() =>
        Assert.True(WorkspaceScan.IsExcludedPath(@"C:\work\build\app\bin\Debug\a.cs", @"C:\work\build\app"));

    /// <summary>The funnel itself, on a real tree: a workspace that lives under a folder named build.</summary>
    [Fact]
    public void AWorkspaceUnderABuildFolder_IsWalked()
    {
        var top  = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"ws-{Guid.NewGuid():N}");
        var root = Path.Combine(top, "build", "app");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "A.cs"), "class A { }");
        try
        {
            Assert.Single(WorkspaceScan.EnumerateFiles(root, "*.cs"));
        }
        finally
        {
            try { Directory.Delete(top, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(@"C:\p\node_modules", true)]
    [InlineData(@"C:\p\node_modules\", true)]
    [InlineData(@"C:\p\src", false)]
    public void DirectoryNamesAreJudgedOnTheirLeaf(string dir, bool skipped) =>
        Assert.Equal(skipped, WorkspaceScan.IsExcludedDirName(dir));
}
