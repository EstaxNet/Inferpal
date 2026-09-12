using System.IO;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>get_solution_info</c> never describes ANOTHER project's solution as the workspace's.
/// </summary>
/// <remarks>
/// Its last resort is <c>last_solution.json</c>, deliberately machine-wide: "the last solution
/// Inferpal knew about". Under VS Code, in a workspace with no <c>.sln</c> — a TypeScript or Python
/// project, the common case — the live search finds nothing and the fallback returned the last
/// solution recorded by Visual Studio, elsewhere on disk: the model read it as "the solution open in
/// the workspace". The right answer is "no solution". And the live search started from the process's
/// current directory, which under VS is not the workspace.
/// </remarks>
[Collection(SignalCollection.Name)]
public class SolutionFallbackTests : IDisposable
{
    private readonly SignalScratchDir _scratch = new();
    private readonly string _base =
        Path.Combine(Path.GetTempPath(), $"inferpal-slnfallback-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { File.Delete(LastKnownSolutionFile.FilePath); } catch { }
        try { Directory.Delete(_base, recursive: true); } catch { }
        _scratch.Dispose();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string WriteSolution(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "Microsoft Visual Studio Solution File, Format Version 12.00\n");
        return path;
    }

    /// <summary>
    /// A root six levels below the test's folder: the live search climbs five levels looking at the
    /// immediate sub-folders, and must never leave the test's tree — <c>%TEMP%</c> holds other
    /// solutions.
    /// </summary>
    private string DeepWorkspace() =>
        Directory.CreateDirectory(Path.Combine(_base, "ws", "a", "b", "c", "d", "e", "root")).FullName;

    private static JsonElement NoArgs() => JsonDocument.Parse("{}").RootElement;

    [Fact]
    public void TheFallback_IsBoundedToTheWorkspace()
    {
        var root  = Path.Combine(_base, "ws");
        var other = Path.Combine(_base, "other", "Other.sln");

        Assert.False(GetSolutionInfoTool.LastKnownApplies(other, root));

        // Witnesses: a solution under the root stays valid, and an unknown root keeps the fallback.
        Assert.True(GetSolutionInfoTool.LastKnownApplies(Path.Combine(root, "src", "Mine.sln"), root));
        Assert.True(GetSolutionInfoTool.LastKnownApplies(other, null));
        Assert.True(GetSolutionInfoTool.LastKnownApplies(other, ""));
    }

    [Fact]
    public async Task AWorkspaceWithoutSolution_IsNotDescribedAsAnotherProjectsSolution()
    {
        var root  = DeepWorkspace();
        var other = WriteSolution(Path.Combine(_base, "other"), "Other.sln");
        LastKnownSolutionFile.Record(other);

        // Witness: the cache does point at the other project's solution.
        Assert.Equal(other, LastKnownSolutionFile.TryReadSolutionPath());

        var result = await new GetSolutionInfoTool(new NullEditorSurface(), () => root)
            .ExecuteAsync(NoArgs(), CancellationToken.None);

        Assert.DoesNotContain("Other.sln", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Solution :", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASolutionRecordedInsideTheWorkspace_IsStillFoundByTheFallback()
    {
        var root = DeepWorkspace();
        // Out of the live search's reach (root and immediate sub-folders): only the fallback can
        // find it.
        var mine = WriteSolution(Path.Combine(root, "src", "deep", "app"), "Mine.sln");
        LastKnownSolutionFile.Record(mine);

        var result = await new GetSolutionInfoTool(new NullEditorSurface(), () => root)
            .ExecuteAsync(NoArgs(), CancellationToken.None);

        Assert.Contains("Solution : Mine.sln", result, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRegistry_GivesTheToolTheAppliedRoot()
    {
        var registry = ConventionCoverageTests.CodeOnly(
            Path.Combine(RepoRoot(), "Inferpal.Core", "Services", "Execution", "ToolRegistry.cs"));

        // Witness: the tool is still registered there.
        Assert.Contains("Register(new GetSolutionInfoTool(", registry, StringComparison.Ordinal);

        Assert.Matches(@"new GetSolutionInfoTool\(editor,\s*\(\)\s*=>\s*indexService\.RootDir\)", registry);
    }
}
