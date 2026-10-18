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

    private string HookRuns => Path.Combine(_repo, ".git", "hook-runs");

    /// <summary>
    /// A repository with one commit, a staged change, and a pre-commit hook that sleeps 3 s; <c>false</c> when this
    /// machine never ran the hook (UNDECIDED: nothing below can be measured — never read as a pass).
    /// </summary>
    /// <remarks>
    /// ⚠ The hook is written BEFORE the first commit, and sleeps only once a flag exists: on the Windows CI runner, a
    /// hook written just before the commit it should slow does not always slow it (the commit answers 0 within the
    /// budget). Each run leaves a mark, so the first commit proves the hook runs here, the measured commit uses a hook
    /// file several git calls old, and a failure says how many times the hook ran.
    /// </remarks>
    private async Task<bool> SlowHookRepo()
    {
        await Git("init -q");
        await Git("config user.email t@example.test");
        await Git("config user.name t");
        await Git("config commit.gpgsign false");
        var hook = Path.Combine(_repo, ".git", "hooks", "pre-commit");
        File.WriteAllText(hook, "#!/bin/sh\necho ran >> .git/hook-runs\n[ -f .git/slow ] || exit 0\nsleep 3\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "1\n");
        await Git("add a.txt");
        await Git("commit -q -m first");
        if (!File.Exists(HookRuns)) return false;
        File.WriteAllText(Path.Combine(_repo, ".git", "slow"), "");
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "2\n");
        await Git("add a.txt");
        return true;
    }

    private string Runs() => File.Exists(HookRuns) ? $"{File.ReadAllLines(HookRuns).Length} hook run(s)" : "the hook never ran";

    [Fact]
    public async Task AGitStoppedAtItsBudget_SaysSo_NotThatGitIsMissing()
    {
        if (!await SlowHookRepo()) return;   // UNDECIDED: this machine does not run git hooks — never read as a pass

        var clock  = System.Diagnostics.Stopwatch.StartNew();
        var result = await GitProcess.RunAsync("commit -m second", _repo, CancellationToken.None, TimeSpan.FromSeconds(1));
        var note   = GitProcess.FailureNote("commit", result);

        Assert.True(note is not null,
            $"git commit answered {result.ExitCode} in {clock.Elapsed.TotalSeconds:0.0} s ({Runs()}): {result.Output}");
        Assert.Contains("no answer within 1s", note);
        Assert.DoesNotContain("installed", note);
    }

    [Fact]
    public async Task CommitExec_LetsTheHookFinish()
    {
        if (!await SlowHookRepo()) return;   // UNDECIDED: this machine does not run git hooks — never read as a pass

        var run = await CommitCommandHandler.ExecuteAsync("second", GitProcess.ForCommit(_repo), CancellationToken.None);

        Assert.True(run.Ok, run.Output);
        Assert.Equal(2, File.ReadAllLines(HookRuns).Length);   // witness: the slow hook did run, and was let finish
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
