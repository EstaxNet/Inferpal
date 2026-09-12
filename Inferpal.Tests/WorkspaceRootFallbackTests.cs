using System.IO;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A tool without a path works in the workspace root, never in the process's working directory —
/// which in Visual Studio is the out-of-process host's folder, not the project.
/// </summary>
/// <remarks>
/// In VS Code the host starts in the workspace, so the two coincided and nothing showed. In Visual
/// Studio, <c>run_tests</c> without a path answered "No test runner detected" on a solution full of
/// tests, and <c>get_diagnostics</c> "no project" instead of building.
/// </remarks>
public sealed class WorkspaceRootFallbackTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-rootfallback-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* cleanup */ }
    }

    private void AssertWorkingDirectoryIsNotTheRoot() =>
        // Witness: if the working directory were the root, these tests would tell nothing apart.
        Assert.NotEqual(Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar),
                        Directory.GetCurrentDirectory().TrimEnd(Path.DirectorySeparatorChar));

    [Fact]
    public void GetDiagnostics_FindsTheProjectUnderTheWorkspaceRoot()
    {
        AssertWorkingDirectoryIsNotTheRoot();
        var project = Path.Combine(_root, "App.csproj");
        File.WriteAllText(project, "<Project />");

        Assert.Equal(project, GetDiagnosticsTool.FindProjectFile(_root));
    }

    [Fact]
    public void RunTests_WithoutAPath_StartsAtTheWorkspaceRoot()
    {
        AssertWorkingDirectoryIsNotTheRoot();

        Assert.Equal(_root, RunTestsTool.ResolveWorkDir(path: null, root: _root));
        // Reference arm: a given path wins over the root.
        var sub = Directory.CreateDirectory(Path.Combine(_root, "tests")).FullName;
        Assert.Equal(sub, RunTestsTool.ResolveWorkDir(path: sub, root: _root));
    }

    [Fact]
    public void WithoutARoot_TheWorkingDirectoryIsStillTheFallback()
    {
        Assert.Equal(Directory.GetCurrentDirectory(), RunTestsTool.ResolveWorkDir(path: null, root: null));
    }
}
