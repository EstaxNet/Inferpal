using System.IO;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A commit gets the time its hooks take, and a git stopped at its budget says so.
//
//  /commit-exec ran git commit under the 15 s budget of a read: a pre-commit hook that formats, lints
//  or builds was killed mid-run, every time. And a git stopped at its budget answered -1, like a git
//  that never started — "git could not be started (is it installed and on PATH?)".
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class GitCommitBudgetTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("inferpal-commitbudget-").FullName;

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_repo, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    private async Task Git(string args) =>
        Assert.Equal(0, (await GitProcess.RunAsync(args, _repo, CancellationToken.None)).ExitCode);

    /// <summary>A repository with one commit, a staged change, and a pre-commit hook that sleeps 3 s.</summary>
    private async Task SlowHookRepo()
    {
        await Git("init -q");
        await Git("config user.email t@example.test");
        await Git("config user.name t");
        await Git("config commit.gpgsign false");
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "1\n");
        await Git("add a.txt");
        await Git("commit -q -m first");
        var hook = Path.Combine(_repo, ".git", "hooks", "pre-commit");
        File.WriteAllText(hook, "#!/bin/sh\nsleep 3\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "2\n");
        await Git("add a.txt");
    }

    [Fact]
    public async Task AGitStoppedAtItsBudget_SaysSo_NotThatGitIsMissing()
    {
        await SlowHookRepo();

        var result = await GitProcess.RunAsync("commit -m second", _repo, CancellationToken.None, TimeSpan.FromSeconds(1));
        var note   = GitProcess.FailureNote("commit", result);

        Assert.NotNull(note);
        Assert.Contains("no answer within 1s", note);
        Assert.DoesNotContain("installed", note);
    }

    [Fact]
    public async Task CommitExec_LetsTheHookFinish()
    {
        await SlowHookRepo();

        var run = await CommitCommandHandler.ExecuteAsync("second", GitProcess.ForCommit(_repo), CancellationToken.None);

        Assert.True(run.Ok, run.Output);
        var log = await GitProcess.RunAsync("log --oneline", _repo, CancellationToken.None);
        Assert.Contains("second", log.Output);
    }

    [Fact]
    public void BothFrontEnds_CommitWithTheCommitBudget()
    {
        Assert.True(GitProcess.CommitTimeout >= TimeSpan.FromMinutes(5));
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var host = ConventionCoverageTests.CodeOnly(Path.Combine(dir!.FullName, "Inferpal.Host", "HostSlashCommands.cs"));
        var vs   = ConventionCoverageTests.CodeOnly(
            Path.Combine(dir.FullName, "Inferpal", "ToolWindow", "InferpalToolWindowData.PromptHistory.cs"));

        foreach (var source in new[] { host, vs })
        {
            var call = source.IndexOf("CommitCommandHandler.ExecuteAsync(", StringComparison.Ordinal);
            Assert.True(call >= 0, "The /commit-exec call is not found.");   // WITNESS
            Assert.Contains("GitProcess.ForCommit(", source.Substring(call, 200), StringComparison.Ordinal);
        }
    }
}
