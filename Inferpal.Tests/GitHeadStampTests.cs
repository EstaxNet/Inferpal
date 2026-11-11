using System.IO;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What a checkout, a pull or a commit moves, read from the git folder's files without running git
/// (<see cref="GitProcess.HeadStamp"/>): a repository's MCP servers follow the code a commit brings by it. Laid out by hand,
/// as git writes them.
/// </summary>
public sealed class GitHeadStampTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-headstamp").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string Put(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>A commit rewrites the ref HEAD names; a checkout rewrites HEAD.</summary>
    [Fact]
    public void ACommitOrACheckout_MovesTheStamp()
    {
        var work = Path.Combine(_root, "repo");
        Put("repo/.git/HEAD", "ref: refs/heads/main\n");
        var main = Put("repo/.git/refs/heads/main", "1111111111111111111111111111111111111111\n");
        var before = GitProcess.HeadStamp(work);
        Assert.NotNull(before);

        File.WriteAllText(main, "2222222222222222222222222222222222222222222\n");   // a commit
        var afterCommit = GitProcess.HeadStamp(work);
        Assert.NotEqual(before, afterCommit);

        Put("repo/.git/refs/heads/next", "3333333333333333333333333333333333333333\n");
        Put("repo/.git/HEAD", "ref: refs/heads/next\n");                            // a checkout
        Assert.NotEqual(afterCommit, GitProcess.HeadStamp(work));
    }

    /// <summary>A linked work tree: HEAD in its own git folder, the refs in the common one.</summary>
    [Fact]
    public void ALinkedWorkTree_FollowsTheRefInTheCommonFolder()
    {
        var work = Path.Combine(_root, "linked");
        Put("main/.git/refs/heads/feature", "1111111111111111111111111111111111111111\n");
        Put("main/.git/worktrees/linked/HEAD", "ref: refs/heads/feature\n");
        Put("main/.git/worktrees/linked/commondir", "../..\n");
        Put("linked/.git", "gitdir: " + Path.Combine(_root, "main", ".git", "worktrees", "linked") + "\n");
        var before = GitProcess.HeadStamp(work);
        Assert.NotNull(before);

        File.WriteAllText(Path.Combine(_root, "main", ".git", "refs", "heads", "feature"), "2222222222222222222222222222222222222222222\n");
        Assert.NotEqual(before, GitProcess.HeadStamp(work));
    }

    /// <summary>A reftable repository keeps a placeholder in HEAD and every ref in reftable/, whose list a commit rewrites.</summary>
    [Fact]
    public void AReftableRepository_FollowsItsTablesList()
    {
        var work = Path.Combine(_root, "rt");
        Put("rt/.git/HEAD", "ref: refs/heads/.invalid\n");
        var list = Put("rt/.git/reftable/tables.list", "0x000000000001-0x000000000001-aaaa.ref\n");
        var before = GitProcess.HeadStamp(work);

        File.WriteAllText(list, "0x000000000001-0x000000000002-bbbbbbbb.ref\n");
        Assert.NotEqual(before, GitProcess.HeadStamp(work));
    }

    /// <summary>Not a work tree: nothing to follow.</summary>
    [Fact]
    public void AFolderOutsideGit_HasNoStamp()
    {
        Directory.CreateDirectory(Path.Combine(_root, "plain"));
        Assert.Null(GitProcess.HeadStamp(Path.Combine(_root, "plain")));
    }
}
