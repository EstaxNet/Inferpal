using System;
using System.Collections.Generic;
using System.Linq;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Covers the project-root resolution order and walk limits extracted from the
// tool-window VM (FindProjectRoot): signal > open-file walk-up > CWD-anchored walk >
// first-open-file parent > CWD. The file system is faked through the injected probes;
// the real signal/CWD/open-paths plumbing stays in the VM.
public class ProjectRootLocatorTests
{
    private static ProjectRootLocator Locator(
        IEnumerable<string>? slnDirs = null,
        Dictionary<string, string[]>? subDirs = null)
    {
        var set = new HashSet<string>(slnDirs ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        return new ProjectRootLocator(
            dirContainsSln: set.Contains,
            getSubDirs:     dir => subDirs is not null && subDirs.TryGetValue(dir, out var s)
                ? s
                : Array.Empty<string>());
    }

    [Fact]
    public void Locate_SignalWins_OverEverything()
    {
        var root = Locator(slnDirs: [TestPaths.P(@"C:\repo")]).Locate(
            [TestPaths.P(@"C:\repo\src\File.cs")], activeSolutionDir: TestPaths.P(@"D:\other"), currentDirectory: TestPaths.P(@"C:\repo"));
        Assert.Equal(TestPaths.P(@"D:\other"), root);
    }

    [Fact]
    public void Locate_WalksUpFromOpenFile_ToTheSlnDir()
    {
        var root = Locator(slnDirs: [TestPaths.P(@"C:\repo")]).Locate(
            [TestPaths.P(@"C:\repo\src\deep\File.cs")], null, TestPaths.P(@"C:\elsewhere"));
        Assert.Equal(TestPaths.P(@"C:\repo"), root);
    }

    [Fact]
    public void Locate_OpenFileWalk_StopsAfterEightLevels()
    {
        // .sln sits 9 levels above the file's directory — out of reach.
        var deep = TestPaths.P(@"C:\repo\a\b\c\d\e\f\g\h\File.cs");
        var root = Locator(slnDirs: [TestPaths.P(@"C:\repo")]).Locate(
            [deep], null, TestPaths.P(@"C:\elsewhere"));
        Assert.NotEqual(TestPaths.P(@"C:\repo"), root); // falls through to the open-file parent
        Assert.Equal(TestPaths.P(@"C:\repo\a\b\c\d\e\f\g\h"), root);
    }

    [Fact]
    public void Locate_CwdWalk_ChecksImmediateSubDirsToo()
    {
        var root = Locator(
            slnDirs: [TestPaths.P(@"C:\work\proj")],
            subDirs: new() { [TestPaths.P(@"C:\work")] = [TestPaths.P(@"C:\work\proj")] })
            .Locate([], null, TestPaths.P(@"C:\work"));
        Assert.Equal(TestPaths.P(@"C:\work\proj"), root);
    }

    [Fact]
    public void Locate_FallsBackToFirstOpenFileParent_ThenCwd()
    {
        var locator = Locator(); // no .sln anywhere
        Assert.Equal(TestPaths.P(@"C:\some\place"), locator.Locate([TestPaths.P(@"C:\some\place\File.cs")], null, TestPaths.P(@"C:\cwd")));
        Assert.Equal(TestPaths.P(@"C:\cwd"),        locator.Locate([], null, TestPaths.P(@"C:\cwd")));
    }

    [Fact]
    public void FindSlnDirFromPaths_PrefersFirstPathThatResolves()
    {
        var dir = Locator(slnDirs: [TestPaths.P(@"C:\repoB")]).FindSlnDirFromPaths(
            [TestPaths.P(@"C:\repoA\src\A.cs"), TestPaths.P(@"C:\repoB\src\B.cs")]);
        Assert.Equal(TestPaths.P(@"C:\repoB"), dir);
    }

    [Fact]
    public void LocateReliable_NullWhenNoSlnAnywhere_InsteadOfFallingBack()
    {
        var locator = Locator(); // no .sln anywhere
        Assert.Null(locator.LocateReliable([TestPaths.P(@"C:\some\place\File.cs")], null, TestPaths.P(@"C:\cwd")));
        Assert.Equal(TestPaths.P(@"D:\signal"), locator.LocateReliable([], TestPaths.P(@"D:\signal"), TestPaths.P(@"C:\cwd")));
    }
}
