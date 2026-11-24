using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Inferpal.Services.Governance;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>git_history</c> answers "when did this code appear, or go, and why" (pari 179): the commits that added or removed a text,
/// each with the lines that did; and <c>blame</c> skips the reformatting commits a repository lists in
/// <c>.git-blame-ignore-revs</c>. Real git. Alone in the exclusions' collection: one test declares ignore files.
/// </summary>
[Collection(ContentExclusionSerialCollection.Name)]
public sealed class GitHistorySearchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-gitsearch-").FullName;

    public void Dispose()
    {
        ContentExclusions.ForgetForTests(_root);
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);   // git marks its objects read-only
            Directory.Delete(_root, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in (string[])["-c", "core.autocrlf=false", "-c", "user.email=t@t", "-c", "user.name=Ada", .. args]) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        // Both pipes drained at once: git waits on a full one.
        var error = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(30_000) && p.ExitCode == 0,
                    $"git {string.Join(' ', args)} failed ({error.Result}): this test is UNDECIDED, not green.");
        return output.Trim();
    }

    private void Commit(string file, string content, string message)
    {
        var full = Path.Combine(_root, file);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        Git("add", "-A");
        Git("commit", "-q", "-m", message);
    }

    private void Repo()
    {
        Git("init", "-q", ".");
        Commit("src/my file.cs", "int a = 1;\nint timeout = 10;\n", "first version");
        Commit("src/my file.cs", "int a = 1;\nint timeout = 30;\n", "raise the timeout for slow disks");
        Commit("other.txt", "x\n", "unrelated change");
    }

    private Task<string> RunAsync(object args)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(args));
        return new GitHistoryTool(() => _root).ExecuteAsync(doc.RootElement.Clone(), CancellationToken.None);
    }

    [Fact]
    public async Task ATextAddedThenRemoved_IsFoundInBothCommits_NewestFirst_WithTheLinesThatDid()
    {
        Repo();

        var report = await RunAsync(new { search = "timeout = 10" });

        var raised = report.IndexOf("raise the timeout for slow disks", StringComparison.Ordinal);
        var first  = report.IndexOf("first version", StringComparison.Ordinal);
        Assert.True(raised >= 0 && first > raised, report);
        Assert.Contains("  -int timeout = 10;", report);   // removed by the newer one
        Assert.Contains("  +int timeout = 10;", report);   // added by the older one, the root commit
        Assert.Contains("src/my file.cs @@", report);      // the file a hunk is in, its name whole
        Assert.DoesNotContain("unrelated change", report);
    }

    [Fact]
    public async Task ARegularExpression_MatchesTheLinesChanged()
    {
        Repo();

        var report = await RunAsync(new { search = "timeout = [0-9]0", regex = true });   // POSIX: git reads no \d

        Assert.Contains("raise the timeout for slow disks", report);
        Assert.Contains("first version", report);
        Assert.Contains("lines matching", report);
    }

    [Fact]
    public async Task ATextNoCommitChanged_IsSaidAbsent_ForThePathSearched()
    {
        Repo();

        var report = await RunAsync(new { search = "retries", path = "src" });

        Assert.Contains("No commit added or removed \"retries\" in src.", report);
    }

    [Fact]
    public async Task TheNextMatches_AreReachedWithSkip()
    {
        Repo();

        var first = await RunAsync(new { search = "timeout = 10", max_count = 1 });
        var next  = await RunAsync(new { search = "timeout = 10", max_count = 1, skip = 1 });

        Assert.Contains("raise the timeout", first);
        Assert.Contains("read them with skip=1", first);
        Assert.Contains("first version", next);
        Assert.DoesNotContain("raise the timeout", next);
    }

    [Fact]
    public async Task AMatchInAFileKeptOut_LeavesNoTrace_NotEvenACount()
    {
        // git searches every file: said or counted, a match in an excluded file would tell what that file holds.
        Repo();
        Commit("secret.txt", "token=abc\n", "store the token");
        File.WriteAllText(Path.Combine(_root, ".aiignore"), "secret.txt\n");
        ContentExclusions.Declare(_root, () => ContentExclusions.DefaultFiles);

        var held   = await RunAsync(new { search = "token=abc" });
        var absent = await RunAsync(new { search = "token=xyz" });

        Assert.Equal(absent.Replace("token=xyz", "token=abc"), held);   // the same answer, whatever the excluded file holds
        Assert.Contains("Files the ignore files keep out are not searched.", held);
    }

    [Fact]
    public async Task ALineThatStartsLikeADiffHeader_IsStillTheLineThatDid()
    {
        Repo();
        Commit("count.c", "int n = 0;\n++retries;\n", "count the retries");

        var report = await RunAsync(new { search = "++retries" });

        Assert.Contains("  +++retries;", report);
    }

    [Fact]
    public async Task AMinifiedLine_IsCut_NotTheWholeAnswer()
    {
        Repo();
        Commit("app.min.js", "var apiUrl='x';" + new string('a', 5000) + "\n", "bundle");

        var report = await RunAsync(new { search = "apiUrl" });

        Assert.Contains("more characters)", report);
        Assert.True(report.Length < 2_000, report.Length.ToString());
    }

    [Theory]
    [InlineData("{\"search\":\"x\",\"commit\":\"HEAD\"}")]
    [InlineData("{\"search\":\"x\",\"start_line\":1}")]
    [InlineData("{\"regex\":true}")]
    [InlineData("{\"search\":\"a\\u0000b\"}")]
    public async Task ACallWrittenForTwoReads_OrThatGitCannotBeGiven_IsRefused(string json)
    {
        Repo();
        using var doc = JsonDocument.Parse(json);

        var report = await new GitHistoryTool(() => _root).ExecuteAsync(doc.RootElement.Clone(), CancellationToken.None);

        Assert.StartsWith("Error:", report);
    }

    [Fact]
    public async Task Blame_SkipsTheReformattingCommitsTheRepositoryLists_AndSaysSo()
    {
        Git("init", "-q", ".");
        Commit("a.cs", "int x=1;\n", "the value");
        Commit("a.cs", "int x = 1;\n", "reformat everything");
        File.WriteAllText(Path.Combine(_root, GitHistoryTool.IgnoreRevsFile), "# formatting\n" + Git("rev-parse", "HEAD") + "\n");

        var report = await RunAsync(new { path = "a.cs", start_line = 1, end_line = 1 });

        Assert.Contains("lists are skipped", report);
        Assert.Contains(Git("rev-parse", "--short=7", "HEAD~1"), report);   // the commit that wrote the line
    }

    [Theory]
    [InlineData("API_KEY=sk-live-123\n", "holds a line that is no commit hash")]               // never shown, never given to git
    [InlineData("0123456789abcdef0123456789abcdef01234567\n", "lists a commit this repository does not have")]
    public async Task AnIgnoreFileNotUsable_IsSaid_AndTheBlameMadeWithoutIt(string content, string said)
    {
        Git("init", "-q", ".");
        Commit("a.cs", "int x = 1;\n", "the value");
        File.WriteAllText(Path.Combine(_root, GitHistoryTool.IgnoreRevsFile), content);

        var report = await RunAsync(new { path = "a.cs", start_line = 1, end_line = 1 });

        Assert.Contains(said, report);
        Assert.Contains("int x = 1;", report);
        Assert.DoesNotContain("API_KEY", report);
    }
}
