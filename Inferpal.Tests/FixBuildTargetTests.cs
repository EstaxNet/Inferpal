using System.IO;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>/fix-build</c> compiled the folder's FIRST solution, not the one open: with <c>Backend.sln</c> and
/// <c>Frontend.sln</c> side by side, "Fix with AI" on Frontend's failing build compiled Backend, which passed, and said
/// "✅ Build succeeded".
/// </summary>
public sealed class FixBuildTargetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"fixbuild-{Guid.NewGuid():N}");
    private readonly string _backend;
    private readonly string _frontend;

    public FixBuildTargetTests()
    {
        Directory.CreateDirectory(_root);
        _backend  = Path.Combine(_root, "Backend.sln");
        _frontend = Path.Combine(_root, "Frontend.sln");
        File.WriteAllText(_backend, string.Empty);
        File.WriteAllText(_frontend, string.Empty);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public void TheSolutionOpenInTheEditor_IsTheOneBuilt()
    {
        Assert.Equal(_frontend, FixBuildTarget.Resolve(typed: null, openSolution: _frontend, _root));
    }

    [Fact]
    public void WithNoSolutionOpen_TheFoldersFirstIsBuilt()
    {
        // Reference arm: the previous choice, kept when nothing better is known.
        Assert.Equal(_backend, FixBuildTarget.Resolve(typed: null, openSolution: null, _root));
    }

    [Fact]
    public void VisualStudiosFixBuild_AsksForTheOpenSolution()
    {
        var vm = ConventionCoverageTests.CodeOnly(Path.Combine(ConventionCoverageTests.RepoRoot(),
            "Inferpal", "ToolWindow", "InferpalToolWindowData.PromptHistory.cs"));
        Assert.Contains("FixBuildTarget.Resolve(", vm, StringComparison.Ordinal);
        Assert.Contains("ActiveSolutionSignal.TryReadSolutionPath()", vm, StringComparison.Ordinal);
        Assert.DoesNotContain("SolutionFiles.FirstIn(", vm, StringComparison.Ordinal);
    }

    [Fact]
    public void APathTyped_WinsOverEverything()
    {
        Assert.Equal("src/App.csproj", FixBuildTarget.Resolve("src/App.csproj", _frontend, _root));
    }
}
