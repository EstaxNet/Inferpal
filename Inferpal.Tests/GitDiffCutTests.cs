using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Services;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>get_git_status</c> caps the diff it returns, and the cap was a wall: "truncated — N more characters" and no
/// way to read the rest. The diff is sorted by path, so the files after the cut were never visible at all — the
/// model reviewed or described the part it saw as if it were the whole change. The cut names the files it cut short
/// or never reached, and <c>diff_path</c> reads one of them.
/// </summary>
public sealed class GitDiffCutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"gitdiff-{Guid.NewGuid():N}");

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

    /// <summary>A repository with one commit, then <paramref name="files"/> rewritten with distinct lines.</summary>
    private void Repo(params (string Name, int Lines)[] files)
    {
        Directory.CreateDirectory(_root);
        Assert.True(Git("init -q ."), "`git init` failed: this test is UNDECIDED, not green.");
        foreach (var (name, _) in files) File.WriteAllText(Path.Combine(_root, name), "first\n");
        Assert.True(Git("add -A") && Git("-c user.email=t@t -c user.name=t commit -q -m init"),
                    "the first commit failed: this test is UNDECIDED, not green.");

        foreach (var (name, lines) in files)
        {
            var body = new StringBuilder();
            for (var i = 0; i < lines; i++) body.Append($"{name} changed line number {i} of the test\n");
            File.WriteAllText(Path.Combine(_root, name), body.ToString());
        }
    }

    private async Task<string> RunAsync(object args)
    {
        var tool = new GetGitStatusTool(new NullEditorSurface(), () => _root);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(args));
        return await tool.ExecuteAsync(doc.RootElement, CancellationToken.None);
    }

    [Fact]
    public async Task ACutDiff_NamesTheFilesItCutShortOrNeverReached_AboveTheDiff()
    {
        Repo(("a.cs", 60), ("b.cs", 60), ("c.cs", 60), ("d.cs", 60));   // ~2 700 characters of diff each

        var report = await RunAsync(new { include_diff = true });

        var diffSection = report[report.IndexOf("=== git diff HEAD ===", StringComparison.Ordinal)..];
        Assert.Contains("a.cs changed line number 0", diffSection);                       // witness: the diff ran
        Assert.Contains("Cut short: c.cs.", diffSection);
        Assert.Contains("Not shown at all (1 file(s)): d.cs.", diffSection);
        Assert.Contains("diff_path=<file>", diffSection);
        Assert.True(diffSection.IndexOf("(diff cut at", StringComparison.Ordinal)
                    < diffSection.IndexOf("diff --git", StringComparison.Ordinal), "the note qualifies the diff: above it");
        Assert.DoesNotContain("d.cs changed line number", report);
    }

    [Fact]
    public async Task DiffPath_ReadsTheDiffOfAFileTheFullDiffNeverReached()
    {
        Repo(("a.cs", 60), ("b.cs", 60), ("c.cs", 60), ("d.cs", 60));

        var report = await RunAsync(new { include_diff = true, diff_path = "d.cs" });

        Assert.Contains("d.cs changed line number 59", report);
        Assert.DoesNotContain("a.cs changed line number", report);
        Assert.DoesNotContain("(diff cut at", report);
    }

    [Fact]
    public async Task ADiffWithinTheCap_SaysNothingAboutACut()
    {
        Repo(("a.cs", 5), ("b.cs", 5));

        var report = await RunAsync(new { include_diff = true });

        Assert.Contains("b.cs changed line number 4", report);
        Assert.DoesNotContain("(diff cut at", report);
    }

    [Fact]
    public void TheCut_FallsOnALineEnd()
    {
        var diff = "diff --git a/x b/x\n" + string.Concat(Enumerable.Range(0, 100).Select(i => $"+line {i}\n"));

        var (shown, note) = GetGitStatusTool.CutDiff(diff, 200, restricted: false);

        Assert.NotNull(note);
        Assert.True(diff.Length > shown.Length && diff[shown.Length] == '\n', "the cut is not on a line end");
        Assert.Contains("Cut short: x.", note);
    }

    private bool Git(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", args)
            {
                WorkingDirectory = _root,
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            })!;
            p.WaitForExit(20_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
