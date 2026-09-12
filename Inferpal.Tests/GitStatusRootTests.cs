using System.IO;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>get_git_status</c> describes the WORKSPACE's repository, never the one the process sits in.
/// </summary>
/// <remarks>
/// Without <c>path</c>, the tool looked for the repository from the open files, then from
/// <c>Directory.GetCurrentDirectory()</c> — and the root it receives only bounded a supplied
/// <c>path</c>. Under Visual Studio the out-of-process host never sits in the workspace: with no file
/// open, the model got the git state of the repository holding the host's folder, or "not a
/// repository" for a project that is one. And a <c>path</c> outside any repository fell back to those
/// same stand-ins instead of saying there is no repository.
/// </remarks>
public class GitStatusRootTests : IDisposable
{
    private readonly string _base =
        Path.Combine(Path.GetTempPath(), $"inferpal-gitroot-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { }
    }

    private static JsonElement NoArgs() => JsonDocument.Parse("{}").RootElement;

    [Fact]
    public async Task WithoutAPath_TheWorkspacesRepositoryIsDescribed()
    {
        var repo = Path.Combine(_base, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var workspace = Directory.CreateDirectory(Path.Combine(repo, "src", "app")).FullName;

        var result = await new GetGitStatusTool(new NullEditorSurface(), () => workspace)
            .ExecuteAsync(NoArgs(), CancellationToken.None);

        Assert.StartsWith($"Repository root: {repo}", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWorkspaceOutsideAnyRepository_IsNotGivenTheProcesssRepository()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(_base, "not-a-repo")).FullName;

        var result = await new GetGitStatusTool(new NullEditorSurface(), () => workspace)
            .ExecuteAsync(NoArgs(), CancellationToken.None);

        Assert.DoesNotContain("Repository root:", result, StringComparison.Ordinal);
    }
}
