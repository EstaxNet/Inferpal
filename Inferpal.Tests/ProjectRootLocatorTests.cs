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
        var root = Locator(slnDirs: [@"C:\repo"]).Locate(
            [@"C:\repo\src\File.cs"], activeSolutionDir: @"D:\other", currentDirectory: @"C:\repo");
        Assert.Equal(@"D:\other", root);
    }

    [Fact]
    public void Locate_WalksUpFromOpenFile_ToTheSlnDir()
    {
        var root = Locator(slnDirs: [@"C:\repo"]).Locate(
            [@"C:\repo\src\deep\File.cs"], null, @"C:\elsewhere");
        Assert.Equal(@"C:\repo", root);
    }

    [Fact]
    public void Locate_OpenFileWalk_StopsAfterEightLevels()
    {
        // .sln sits 9 levels above the file's directory — out of reach.
        var deep = @"C:\repo\a\b\c\d\e\f\g\h\File.cs";
        var root = Locator(slnDirs: [@"C:\repo"]).Locate(
            [deep], null, @"C:\elsewhere");
        Assert.NotEqual(@"C:\repo", root); // falls through to the open-file parent
        Assert.Equal(@"C:\repo\a\b\c\d\e\f\g\h", root);
    }

    [Fact]
    public void Locate_CwdWalk_ChecksImmediateSubDirsToo()
    {
        var root = Locator(
            slnDirs: [@"C:\work\proj"],
            subDirs: new() { [@"C:\work"] = [@"C:\work\proj"] })
            .Locate([], null, @"C:\work");
        Assert.Equal(@"C:\work\proj", root);
    }

    [Fact]
    public void Locate_FallsBackToFirstOpenFileParent_ThenCwd()
    {
        var locator = Locator(); // no .sln anywhere
        Assert.Equal(@"C:\some\place", locator.Locate([@"C:\some\place\File.cs"], null, @"C:\cwd"));
        Assert.Equal(@"C:\cwd",        locator.Locate([], null, @"C:\cwd"));
    }

    [Fact]
    public void FindSlnDirFromPaths_PrefersFirstPathThatResolves()
    {
        var dir = Locator(slnDirs: [@"C:\repoB"]).FindSlnDirFromPaths(
            [@"C:\repoA\src\A.cs", @"C:\repoB\src\B.cs"]);
        Assert.Equal(@"C:\repoB", dir);
    }

    [Fact]
    public void LocateReliable_NullWhenNoSlnAnywhere_InsteadOfFallingBack()
    {
        var locator = Locator(); // no .sln anywhere
        Assert.Null(locator.LocateReliable([@"C:\some\place\File.cs"], null, @"C:\cwd"));
        Assert.Equal(@"D:\signal", locator.LocateReliable([], @"D:\signal", @"C:\cwd"));
    }
}
