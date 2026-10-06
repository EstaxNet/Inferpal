using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Inferpal.Services.Editor;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  Before the first commit, the diff shows what `git add` staged.
//
//  With no HEAD, get_git_status fell back to `git diff`, which compares the working tree with the INDEX: a file
//  added with `git add` was not in it — "(nothing to diff)" under a status that listed "A  b.txt". The work is now
//  compared with the empty tree, and only when there is no HEAD.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class GitNoCommitDiffTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-git-nocommit-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private bool Git(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", args)
            {
                WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            })!;
            p.WaitForExit(30_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private async Task<string> StatusAsync(bool includeDiff = false)
    {
        var tool = new GetGitStatusTool(new NullEditorSurface(), () => _root);
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { path = _root, include_diff = includeDiff }));
        return await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
    }

    private static string Section(string report, string heading)
    {
        var start = report.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no section '{heading}' in:\n{report}");
        var end = report.IndexOf("\n===", start + heading.Length, StringComparison.Ordinal);
        return end < 0 ? report[start..] : report[start..end];
    }

    [Fact]
    public async Task BeforeTheFirstCommit_TheSummaryListsWhatWasStaged()
    {
        Assert.True(Git("init -q ."), "`git init` failed: UNDECIDED, not green.");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "hello\n");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "world\n");
        Assert.True(Git("add a.txt b.txt"), "`git add` failed: UNDECIDED, not green.");

        var report = await StatusAsync(includeDiff: true);

        var summary = Section(report, "=== diff summary");
        Assert.Contains("a.txt", summary);
        Assert.Contains("b.txt", summary);
        Assert.Contains("2 files changed", summary);
        Assert.Contains("+world", Section(report, "=== git diff"));
    }

    [Fact]
    public async Task ACleanRepositoryWithCommits_StillHasNothingToDiff()
    {
        // Reference arm: with a HEAD, the empty tree is never the baseline — or the whole project would read as added.
        Assert.True(Git("init -q ."), "`git init` failed: UNDECIDED, not green.");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "hello\n");
        Assert.True(Git("add a.txt"), "`git add` failed: UNDECIDED, not green.");
        Assert.True(Git("-c user.email=t@t -c user.name=t commit -q -m one"), "`git commit` failed: UNDECIDED, not green.");

        var summary = Section(await StatusAsync(), "=== diff summary (vs HEAD) ===");

        Assert.Contains("(nothing to diff)", summary);
        Assert.DoesNotContain("a.txt", summary);
    }
}
