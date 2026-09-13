using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Models;
using Inferpal.Services.Execution;
using Inferpal.Services.Tasks;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A background task never mixes two projects: if the workspace changes while it runs, its tools stop
/// answering instead of answering about the other project.
/// </summary>
/// <remarks>
/// Tools read the LIVE root on every call (<c>() =&gt; indexService.RootDir</c>). Under Visual Studio,
/// opening another solution while a task started on A was running made its next calls answer about
/// B: a report describing two projects as one, and in proposal mode diffs recorded on B's files for
/// an objective meant for A. Under VS Code the host's root never changes and the guard never bites.
/// </remarks>
public class TaskRootGuardTests
{
    private sealed class CountingRegistry : IToolRegistry
    {
        public List<string> Executed { get; } = [];
        public IReadOnlyList<ToolDefinition> Definitions => [];
        public DiffInfo? ConsumeDiff() => null;

        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        {
            Executed.Add(name);
            return Task.FromResult("inner ran");
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public async Task OnceTheWorkspaceChanged_TheTasksToolsStopAnswering()
    {
        var root     = Path.Combine(Path.GetTempPath(), "inferpal-task", "solution-a");
        var inner    = new CountingRegistry();
        var registry = new BackgroundTaskToolRegistry(inner, currentRoot: () => root);

        // Witness: while the workspace is the one the task started in, the tool goes through.
        Assert.Equal("inner ran", await registry.ExecuteAsync("read_file", default, CancellationToken.None));

        root = Path.Combine(Path.GetTempPath(), "inferpal-task", "solution-b");
        var result = await registry.ExecuteAsync("read_file", default, CancellationToken.None);

        Assert.Equal(["read_file"], inner.Executed);   // the second call never reached the tools
        Assert.Contains("workspace changed", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAKnownRoot_TheTaskRunsAsBefore()
    {
        var inner    = new CountingRegistry();
        var registry = new BackgroundTaskToolRegistry(inner, currentRoot: () => null);

        Assert.Equal("inner ran", await registry.ExecuteAsync("read_file", default, CancellationToken.None));
        Assert.Equal("inner ran", await registry.ExecuteAsync("read_file", default, CancellationToken.None));
    }

    [Fact]
    public void BothFrontEnds_GiveTheTaskRegistryTheLiveRoot()
    {
        var vm   = ConventionCoverageTests.CodeOnly(
            Path.Combine(RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.SlashCommands.cs"));
        var host = ConventionCoverageTests.CodeOnly(
            Path.Combine(RepoRoot(), "Inferpal.Host", "HostSlashCommands.cs"));

        // Witness: both constructions (read-only, proposals) are still there, on both sides.
        Assert.Equal(2, Regex.Matches(vm,   @"new BackgroundTaskToolRegistry\(").Count);
        Assert.Equal(2, Regex.Matches(host, @"new BackgroundTaskToolRegistry\(").Count);

        // Lazy: both constructions sit in ONE ternary statement, and a greedy pattern would swallow
        // them in one go — counting 1 on a complete wiring.
        Assert.Equal(2, Regex.Matches(vm,   @"new BackgroundTaskToolRegistry\([^;]*?currentRoot:").Count);
        Assert.Equal(2, Regex.Matches(host, @"new BackgroundTaskToolRegistry\([^;]*?currentRoot:").Count);
    }
}
