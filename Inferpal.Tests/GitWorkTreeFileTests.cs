using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Services;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ A git work tree is marked by a <c>.git</c> FOLDER — or a <c>.git</c> FILE, in a worktree (<c>git worktree add</c>)
/// and a submodule. Three readers looked for the folder only and climbed past such a work tree to the repository above:
/// <c>get_git_status</c> reported the main checkout's branch and diff for a worktree, snapshots went to the parent
/// repository's history folder, and the <c>.gitignore</c> patch skipped the work tree. One reader now answers,
/// <see cref="GitProcess.WorkTreeOf"/>, and no other code tests for <c>.git</c>.
/// </summary>
public sealed class GitWorkTreeFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"gitwt-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);   // git marks its objects read-only
            Directory.Delete(_root, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    private bool Git(string args, string dir)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", args)
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            })!;
            p.WaitForExit(20_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>A repository with one commit on "main", and a worktree on branch "feature" INSIDE it.</summary>
    private (string Main, string Worktree) RepoWithAWorktreeInside()
    {
        var main = Path.Combine(_root, "main");
        Directory.CreateDirectory(main);
        File.WriteAllText(Path.Combine(main, "a.cs"), "class A {}\n");
        Assert.True(Git("init -q -b main .", main)
                    && Git("add -A", main)
                    && Git("-c user.email=t@t -c user.name=t commit -q -m init", main)
                    && Git("worktree add -q -b feature wt", main),
                    "the repository or its worktree could not be made: this test is UNDECIDED, not green.");
        var worktree = Path.Combine(main, "wt");
        Assert.True(File.Exists(Path.Combine(worktree, ".git")), "a worktree's .git is a file");   // WITNESS
        return (main, worktree);
    }

    [Fact]
    public async Task GitStatus_OfAWorktree_IsTheWorktreesOwn()
    {
        var (_, worktree) = RepoWithAWorktreeInside();
        var tool = new GetGitStatusTool(new NullEditorSurface(), () => worktree);

        using var doc = JsonDocument.Parse("{}");
        var report = await tool.ExecuteAsync(doc.RootElement, CancellationToken.None);

        Assert.Contains($"Repository root: {worktree}", report, StringComparison.Ordinal);
        Assert.Contains("feature", report, StringComparison.Ordinal);
    }

    [Fact]
    public void ASnapshotInAWorktree_GoesToTheWorktreesHistory()
    {
        var (_, worktree) = RepoWithAWorktreeInside();

        Assert.Equal(Path.Combine(worktree, ".inferpal", "history"),
                     FileHistoryService.GetHistoryDir(Path.Combine(worktree, "a.cs")));
    }

    [Fact]
    public void ASubmodulesGitFile_MarksAWorkTree()
    {
        var parent = Path.Combine(_root, "parent");
        var sub    = Path.Combine(parent, "libs", "sub");
        Directory.CreateDirectory(Path.Combine(parent, ".git"));
        Directory.CreateDirectory(Path.Combine(sub, "src"));
        File.WriteAllText(Path.Combine(sub, ".git"), "gitdir: ../../.git/modules/sub\n");

        Assert.Equal(sub, GitProcess.WorkTreeOf(Path.Combine(sub, "src")));
        Assert.Equal(parent, GitProcess.WorkTreeOf(Path.Combine(parent, "libs")));   // reference arm: a folder is one too
        Assert.True(GitProcess.IsWorkTreeRoot(sub));
        Assert.False(GitProcess.IsWorkTreeRoot(Path.Combine(sub, "src")));
    }

    [Fact]
    public void NoOtherCode_TestsForDotGit()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Inferpal.sln"))) root = root.Parent;
        Assert.NotNull(root);

        var files = new[] { "Inferpal.Core", "Inferpal", "Inferpal.Host" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(root!.FullName, p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .ToList();
        Assert.True(files.Count > 300, $"only {files.Count} file(s) read: the scan reads nothing");   // WITNESS

        var offenders = files
            .Where(f => Path.GetFileName(f) != "GitProcess.cs")
            .Where(f => Regex.IsMatch(ConventionCoverageTests.CodeOnly(f), @"Path\.Combine\([^;]*""\.git""\s*\)"))
            .Select(Path.GetFileName)
            .ToList();
        Assert.True(offenders.Count == 0,
            "These test for .git themselves — a worktree or a submodule has a .git FILE; use GitProcess.WorkTreeOf: "
            + string.Join(", ", offenders));
    }
}
