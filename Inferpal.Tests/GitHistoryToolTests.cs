using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>git_history</c> reads why code is the way it is: the commits that touched a path, who changed each line of a range,
/// one commit shown. Read-only, offered only in a git work tree, and a commit the model names never reaches git as an
/// option.
/// </summary>
public sealed class GitHistoryToolTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-githistory-").FullName;

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

    private bool Git(string args)
    {
        var psi = new ProcessStartInfo("git", args) { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return p.ExitCode == 0;
    }

    private void Commit(string file, string content, string message)
    {
        var full = Path.Combine(_root, file);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        Assert.True(Git("add -A") && Git($"-c user.email=t@t -c user.name=Ada commit -q -m \"{message}\""),
                    "the commit failed: this test is UNDECIDED, not green.");
    }

    private void Repo()
    {
        Assert.True(Git("init -q ."), "`git init` failed: this test is UNDECIDED, not green.");
        Commit("src/my file.cs", "int a = 1;\nint timeout = 10;\n", "first version");
        Commit("src/my file.cs", "int a = 1;\nint timeout = 30;\n", "raise the timeout for slow disks");
        Commit("other.txt", "x\n", "unrelated change");
    }

    private Task<string> RunAsync(object args, string? root = null)
    {
        var tool = new GitHistoryTool(() => root ?? _root);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(args));
        return tool.ExecuteAsync(doc.RootElement.Clone(), CancellationToken.None);
    }

    [Fact]
    public async Task ThePathsHistory_ListsTheCommitsThatTouchedIt_NewestFirst_AndOnlyThose()
    {
        Repo();

        var report = await RunAsync(new { path = "src/my file.cs" });   // a space in the path stays one argument

        var raised = report.IndexOf("raise the timeout for slow disks", StringComparison.Ordinal);
        var first  = report.IndexOf("first version", StringComparison.Ordinal);
        Assert.True(raised >= 0 && first > raised, report);
        Assert.DoesNotContain("unrelated change", report);
        Assert.Contains("Ada", report);
        Assert.DoesNotContain("older commits exist", report);
    }

    [Fact]
    public async Task AFullPage_SaysOlderCommitsExist_AndHowToReachThem()
    {
        Repo();

        var report = await RunAsync(new { max_count = 1 });

        Assert.Contains("unrelated change", report);
        Assert.DoesNotContain("first version", report);
        Assert.Contains("older commits exist: raise max_count", report);
    }

    [Fact]
    public async Task AMaxCountOutOfBounds_IsClamped_AndSaidAboveTheReport()
    {
        Repo();

        var report = await RunAsync(new { max_count = 5000 });

        Assert.StartsWith(ClampedArgument.NotePrefix + "'max_count' was 5000", report);
        Assert.Contains("first version", report);
    }

    [Fact]
    public async Task Blame_NamesTheCommitThatLastChangedEachLine()
    {
        Repo();

        var report = await RunAsync(new { path = "src/my file.cs", start_line = 2, end_line = 2 });

        Assert.Contains("int timeout = 30;", report);
        Assert.Contains("Ada", report);
        Assert.DoesNotContain("int a = 1;", report);   // only the range asked
    }

    [Fact]
    public async Task ACommit_IsShownWithItsMessageAndDiff()
    {
        Repo();

        var report = await RunAsync(new { commit = "HEAD~1" });

        Assert.Contains("raise the timeout for slow disks", report);
        Assert.Contains("-int timeout = 10;", report);
        Assert.Contains("+int timeout = 30;", report);
        Assert.Contains("diff --git a/src/my file.cs b/src/my file.cs", report);
    }

    [Theory]
    [InlineData("--output=/tmp/x")]
    [InlineData("HEAD; rm -rf /")]
    [InlineData("-p")]
    public async Task ACommitThatIsNoCommitName_IsRefused_BeforeGit(string commit)
    {
        Repo();

        var report = await RunAsync(new { commit });

        Assert.StartsWith("Error:", report);
        Assert.Contains("is not a commit name", report);
    }

    [Fact]
    public async Task ABlobOrAPathInsteadOfACommit_IsRefused_SoNoFileIsPrintedPastTheExclusions()
    {
        Repo();
        var blob = new ProcessStartInfo("git", "rev-parse HEAD:other.txt") { WorkingDirectory = _root, RedirectStandardOutput = true };
        string sha;
        using (var p = Process.Start(blob)!) { sha = p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit(); }
        Assert.Equal(40, sha.Length);   // witness: the blob exists

        var byHash = await RunAsync(new { commit = sha });
        var byPath = await RunAsync(new { commit = "HEAD:other.txt" });

        Assert.StartsWith("Error:", byHash);
        Assert.Contains("names no commit", byHash);   // never the blob's content
        Assert.StartsWith("Error:", byPath);
    }

    [Fact]
    public async Task Blame_ReadsTheCommittedFile_NotTheWorkingTree()
    {
        Repo();
        File.WriteAllText(Path.Combine(_root, "src", "my file.cs"), "int a = 1;\nint timeout = 99;\n");

        var report = await RunAsync(new { path = "src/my file.cs", start_line = 2, end_line = 2 });

        Assert.Contains("int timeout = 30;", report);
        // The line, never a bare "99": the report also shows the commit's hash, which holds "99" by chance.
        Assert.DoesNotContain("timeout = 99", report);
        Assert.Contains("uncommitted changes are not shown", report);
    }

    [Fact]
    public async Task AnEmptyRepository_SaysThereIsNoHistoryYet()
    {
        Assert.True(Git("init -q ."), "`git init` failed: this test is UNDECIDED, not green.");
        var report = await RunAsync(new { });
        Assert.Contains("no commit yet", report);
    }

    [Fact]
    public async Task AnUnknownCommit_SaysGitRefused_NeverAnEmptyHistory()
    {
        Repo();

        var report = await RunAsync(new { commit = "deadbeefdeadbeef" });

        Assert.Contains("names no commit", report);
        Assert.DoesNotContain("No commit touches", report);
    }

    [Fact]
    public void ItIsOffered_InAGitWorkTree_Only()
    {
        var bare = new GitHistoryTool(() => _root);
        Assert.False(bare.IsOffered);                        // no repository yet
        Repo();
        Assert.True(new GitHistoryTool(() => _root).IsOffered);
        Assert.False(new GitHistoryTool(() => null).IsOffered);
    }

    [Fact]
    public async Task APathOutsideTheWorkspace_IsRefused()
    {
        Repo();

        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(new { path = "../.." }));
    }
}
