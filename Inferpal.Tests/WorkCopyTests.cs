using System.Diagnostics;
using System.IO;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The copy a run works in (pari 106): a git worktree on a branch of its own, outside the repository; the person's uncommitted
/// changes named and left out; the copy's changes read back against the start commit, created and deleted files included;
/// discarding removes the copy and its branch — and the person's files never move.
/// </summary>
[Collection(WorkCopyCollection.Name)]
public sealed class WorkCopyTests : IDisposable
{
    private readonly DirectoryInfo _repo = Directory.CreateTempSubdirectory("inferpal-workcopy-repo-");
    private readonly DirectoryInfo _scratch = Directory.CreateTempSubdirectory("inferpal-workcopy-scratch-");

    public void Dispose()
    {
        foreach (var dir in new[] { _repo, _scratch })
            try { Clear(dir.FullName); dir.Delete(recursive: true); } catch { }
    }

    // Git writes its objects read-only: cleared first, or the folder stays behind.
    private static void Clear(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
    }

    private bool Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _repo.FullName, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-c", "user.email=test@example.com", "-c", "user.name=Test", "-c", "commit.gpgsign=false" }.Concat(args))
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        return p.WaitForExit(30_000) && p.ExitCode == 0;
    }

    private string Repo(string relative) => Path.Combine(_repo.FullName, relative);

    private void Commit()
    {
        File.WriteAllText(Repo("Pay.cs"), "class Pay\n{\n    int A() => 1;\n    int B() => 2;\n}\n");
        File.WriteAllText(Repo("Old.cs"), "class Old { }\n");
        Assert.True(Git("init", "-q", ".") && Git("add", "-A") && Git("commit", "-q", "-m", "start"), "git is needed: this test is UNDECIDED, not green.");
    }

    [Fact]
    public async Task TheUncommittedChanges_AreNamed_OneByOne()
    {
        Commit();
        File.WriteAllText(Repo("Pay.cs"), "changed\n");
        File.WriteAllText(Repo("New notes.txt"), "mine\n");

        var uncommitted = await WorkCopy.UncommittedAsync(_repo.FullName, CancellationToken.None);

        Assert.NotNull(uncommitted);
        Assert.Contains("Pay.cs", uncommitted!);
        Assert.Contains("New notes.txt", uncommitted!);
    }

    [Fact]
    public async Task ACopy_IsMadeOutsideTheRepository_OnItsOwnBranch_AndItsChangesAreReadBack_ThenDiscarded()
    {
        Commit();
        File.WriteAllText(Repo("Pay.cs"), "the person's work in progress\n");
        var mine = File.ReadAllText(Repo("Pay.cs"));

        var (copy, refusal) = await WorkCopy.CreateAsync(_repo.FullName, CancellationToken.None);

        Assert.Null(refusal);
        Assert.StartsWith(WorkCopy.Home, copy!.Folder, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("inferpal/1", copy.Branch);
        // From the last commit: the person's uncommitted change is not in the copy.
        Assert.Equal("class Pay\n{\n    int A() => 1;\n    int B() => 2;\n}\n", File.ReadAllText(Path.Combine(copy.Folder, "Pay.cs")).Replace("\r\n", "\n"));

        File.WriteAllText(Path.Combine(copy.Folder, "Pay.cs"), "class Pay\n{\n    int A() => 10;\n    int B() => 2;\n    int C() => 3;\n}\n");
        File.WriteAllText(Path.Combine(copy.Folder, "Gateway.cs"), "class Gateway { }\n");
        File.Delete(Path.Combine(copy.Folder, "Old.cs"));

        var (changes, problem) = await copy.ChangesAsync(CancellationToken.None);

        Assert.Null(problem);
        var pay = Assert.Single(changes, c => c.Path == "Pay.cs");
        Assert.Equal(2, pay.Plan.Hunks.Count);
        Assert.Null(Assert.Single(changes, c => c.Path == "Gateway.cs").Before);
        Assert.Null(Assert.Single(changes, c => c.Path == "Old.cs").After);
        Assert.Equal(mine, File.ReadAllText(Repo("Pay.cs")));
        Assert.True(File.Exists(Repo("Old.cs")));
        Assert.False(File.Exists(Repo("Gateway.cs")));

        Assert.Null(await copy.DiscardAsync(CancellationToken.None));
        Assert.False(Directory.Exists(copy.Folder));
        Assert.False(Git("rev-parse", "--verify", "inferpal/1"));
        Assert.Equal(mine, File.ReadAllText(Repo("Pay.cs")));
    }

    [Fact]
    public async Task OnlyTheKeptHunks_AreWritten_AFileChangedMeanwhile_IsLeft_AndUndoRunGivesEverythingBack()
    {
        Commit();
        var (copy, _) = await WorkCopy.CreateAsync(_repo.FullName, CancellationToken.None);
        File.WriteAllText(Path.Combine(copy!.Folder, "Pay.cs"), "class Pay\n{\n    int A() => 10;\n    int B() => 2;\n    int C() => 3;\n}\n");
        File.WriteAllText(Path.Combine(copy.Folder, "Gateway.cs"), "class Gateway { }\n");
        File.Delete(Path.Combine(copy.Folder, "Old.cs"));
        var (changes, _) = await copy.ChangesAsync(CancellationToken.None);
        var pay = changes.Single(c => c.Path == "Pay.cs");
        var kept = new Dictionary<string, IReadOnlyCollection<int>>
        {
            // The first hunk (A() => 10) dropped, the second (C()) kept.
            ["Pay.cs"] = [pay.Plan.Hunks[1].Index],
            ["Gateway.cs"] = [1],
            ["Old.cs"] = changes.Single(c => c.Path == "Old.cs").Plan.Hunks.Select(h => h.Index).ToList(),
        };
        var history = new FileHistoryService();

        CopyApplied applied;
        using (history.BeginRunScope())
            applied = await WorkCopy.ApplyAsync(_repo.FullName, changes, kept, history, CancellationToken.None);

        Assert.Empty(applied.Left);
        var paid = File.ReadAllText(Repo("Pay.cs")).Replace("\r\n", "\n");
        Assert.Contains("int A() => 1;", paid);
        Assert.Contains("int C() => 3;", paid);
        Assert.True(File.Exists(Repo("Gateway.cs")));
        Assert.False(File.Exists(Repo("Old.cs")));

        await history.UndoRunAsync(history.Runs[0], CancellationToken.None);

        Assert.Equal("class Pay\n{\n    int A() => 1;\n    int B() => 2;\n}\n", File.ReadAllText(Repo("Pay.cs")).Replace("\r\n", "\n"));
        Assert.False(File.Exists(Repo("Gateway.cs")));
        Assert.True(File.Exists(Repo("Old.cs")));
        await copy.DiscardAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AFileThePersonChanged_DuringTheRun_IsLeftAsItIs_AndNamed()
    {
        Commit();
        var (copy, _) = await WorkCopy.CreateAsync(_repo.FullName, CancellationToken.None);
        File.WriteAllText(Path.Combine(copy!.Folder, "Pay.cs"), "class Pay { }\n");
        File.WriteAllText(Repo("Pay.cs"), "the person's own edit\n");
        var (changes, _) = await copy.ChangesAsync(CancellationToken.None);
        var history = new FileHistoryService();

        CopyApplied applied;
        using (history.BeginRunScope())
            applied = await WorkCopy.ApplyAsync(_repo.FullName, changes,
                new Dictionary<string, IReadOnlyCollection<int>> { ["Pay.cs"] = changes[0].Plan.Hunks.Select(h => h.Index).ToList() },
                history, CancellationToken.None);

        Assert.Empty(applied.Written);
        var left = Assert.Single(applied.Left);
        Assert.Equal("Pay.cs", left.Path);
        Assert.Equal(CopyLeftReason.ChangedSince, left.Reason);
        Assert.Equal(Inferpal.Localization.Strings.CopyLeftChangedSince, left.Why);
        Assert.Equal("the person's own edit\n", File.ReadAllText(Repo("Pay.cs")));
        await copy.DiscardAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ASecondCopy_TakesTheNextBranch()
    {
        Commit();
        var (first, _) = await WorkCopy.CreateAsync(_repo.FullName, CancellationToken.None);
        var (second, _) = await WorkCopy.CreateAsync(_repo.FullName, CancellationToken.None);

        Assert.Equal("inferpal/1", first!.Branch);
        Assert.Equal("inferpal/2", second!.Branch);
        await first.DiscardAsync(CancellationToken.None);
        await second.DiscardAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AFolderADiscardLeft_IsPassedOver_NeverRemovedByTheNextCopy()
    {
        Commit();
        var (first, _) = await WorkCopy.CreateAsync(_repo.FullName, CancellationToken.None);
        await first!.DiscardAsync(CancellationToken.None);
        // What a discard that could not delete everything leaves — or a copy another editor has just made: a folder.
        Directory.CreateDirectory(Path.Combine(first.Folder, "src"));
        File.WriteAllText(Path.Combine(first.Folder, "src", "left.txt"), "left");

        var (next, refusal) = await WorkCopy.CreateAsync(_repo.FullName, CancellationToken.None);

        Assert.True(next is not null, refusal);
        Assert.NotEqual(first.Folder, next!.Folder);
        Assert.True(File.Exists(Path.Combine(first.Folder, "src", "left.txt")));                     // untouched
        await next.DiscardAsync(CancellationToken.None);
    }

    [Fact]
    /// <remarks>What a discard removes itself, once git has left it. Windows' enumeration never descends a folder link: there
    /// this holds the removal; the POSIX legs, where it would descend, hold that the link is never followed.</remarks>
    public async Task WhatADiscardRemovesItself_RemovesALink_NeverWhatItLeadsTo()
    {
        var outside = Directory.CreateTempSubdirectory("inferpal-workcopy-outside-");
        var left = Path.Combine(_scratch.FullName, "left");
        try
        {
            var kept = Path.Combine(outside.FullName, "kept.txt");
            File.WriteAllText(kept, "the person's");
            File.SetAttributes(kept, FileAttributes.ReadOnly);
            Directory.CreateDirectory(left);
            File.WriteAllText(Path.Combine(left, "built.dll"), "x");
            File.SetAttributes(Path.Combine(left, "built.dll"), FileAttributes.ReadOnly);
            try { Directory.CreateSymbolicLink(Path.Combine(left, "linked"), outside.FullName); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;   // undecided here: this machine cannot create links without a privilege
            }

            var problem = await WorkCopy.RemoveFolderAsync(left, CancellationToken.None);

            Assert.Null(problem);
            Assert.False(Directory.Exists(left));                                                    // read-only file included
            Assert.Equal("the person's", File.ReadAllText(kept));
            Assert.True(File.GetAttributes(kept).HasFlag(FileAttributes.ReadOnly));                  // never reached
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(outside.FullName)) File.SetAttributes(f, FileAttributes.Normal);
            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AFolderWithNoCommit_IsRefused_AndSaysWhy()
    {
        Assert.True(Git("init", "-q", "."), "git is needed: this test is UNDECIDED, not green.");

        var (copy, refusal) = await WorkCopy.CreateAsync(_repo.FullName, CancellationToken.None);

        Assert.Null(copy);
        Assert.Contains("a copy starts from the last commit", refusal);
    }
}

/// <summary>The tests that make copies of repositories: one at a time, as git's worktrees of one repository are.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorkCopyCollection
{
    public const string Name = "WorkCopy";
}
