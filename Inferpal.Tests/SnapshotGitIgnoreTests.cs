using System.IO;
using Inferpal.Services;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  File-history snapshots never show up in `git status`.
//
//  A snapshot is a copy of whatever the agent overwrote, secrets included, kept at the GIT root. The only
//  rule ignoring it was the line indexing adds to the .gitignore of the WORKSPACE root, and only when that
//  root holds the .git: a solution below the repository root (repo/src/App.sln) or RAG turned off, and the
//  snapshots were untracked files — the ones "Stage All" in either editor commits.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class SnapshotGitIgnoreTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("inferpal-snapgit").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch { }
    }

    private async Task<string> GitAsync(string args)
    {
        var (output, exit) = await GitProcess.RunAsync(args, _repo, CancellationToken.None);
        Assert.True(exit == 0, $"git {args} failed ({exit}): {output} — this test is UNDECIDED, not green.");
        return output;
    }

    [Fact]
    public async Task ASnapshotAtTheRepositoryRoot_IsNotAnUntrackedFile_WhenTheSolutionLivesBelowIt()
    {
        await GitAsync("init -q");
        var source = Path.Combine(_repo, "src", "appsettings.Development.json");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, "{ \"ApiKey\": \"secret\" }");

        var snapshot = await new FileHistoryService().SnapshotAsync(source, CancellationToken.None);
        Assert.StartsWith(Path.Combine(_repo, ".inferpal", "history"), snapshot);   // witness: at the git root

        var status = await GitAsync("status --porcelain --untracked-files=all");

        Assert.Contains("src/appsettings.Development.json", status);   // witness: untracked files are listed
        Assert.DoesNotContain(".inferpal", status);
    }

    [Fact]
    public async Task TheIgnoreFile_IsNotTakenForASnapshot()
    {
        // Reference arm: what makes git ignore the folder lives IN it, and restoring still finds the snapshot.
        await GitAsync("init -q");
        var source = Path.Combine(_repo, "a.cs");
        await File.WriteAllTextAsync(source, "class A {}");
        var history = new FileHistoryService();

        var snapshot = await history.SnapshotAsync(source, CancellationToken.None);

        Assert.Equal(snapshot, history.FindMostRecentSnapshot(source));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(snapshot)!), f => f.EndsWith(FileHistoryService.SnapshotExtension));
    }
}
