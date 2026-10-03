using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Inferpal.Services;
using Inferpal.Services.Agent;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>get_git_status</c> reaches the model as ONE tool result, cut in its MIDDLE past the loop's cap. A repository with
/// three hundred branches and a hundred and fifty changed files answered 24 957 characters: the cut fell on the
/// branches and the start of the diff summary — the section that says what changed. Each long section keeps what fits,
/// the branches most recent first with the current one always shown, and the diff summary keeps its total.
/// </summary>
public sealed class GitStatusBudgetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"gitbudget-{Guid.NewGuid():N}");

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
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "-c core.autocrlf=false " + args)
            {
                WorkingDirectory = _root,
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            })!;
            // Read both pipes: a hundred files staged write a warning each, and an unread pipe blocks git.
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            return p.WaitForExit(60_000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    private void Repo(int branches, int changedFiles)
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Assert.True(Git("init -q ."), "`git init` failed: this test is UNDECIDED, not green.");
        for (var i = 0; i < changedFiles; i++)
            File.WriteAllText(Path.Combine(_root, "src", $"Module{i:D3}.cs"), "// first\n");
        Assert.True(Git("add -A") && Git("-c user.email=t@t -c user.name=t commit -q -m init"),
                    "the first commit failed: this test is UNDECIDED, not green.");
        for (var i = 0; i < branches; i++)
            Git($"branch feature/JIRA-{1000 + i}-improve-the-checkout-flow-part-{i}");
        for (var i = 0; i < changedFiles; i++)
            File.WriteAllText(Path.Combine(_root, "src", $"Module{i:D3}.cs"), $"// changed {i}\n// and longer\n");
    }

    private async Task<string> RunAsync(object args)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(args));
        return await new GetGitStatusTool(new NullEditorSurface(), () => _root)
            .ExecuteAsync(doc.RootElement, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABusyRepository_ReachesTheModelWhole_ItsSummaryTotalAndCurrentBranchIncluded(bool includeDiff)
    {
        Repo(branches: 300, changedFiles: 150);

        var report = await RunAsync(new { include_diff = includeDiff });

        Assert.Contains("=== git branch -a (most recent first) ===", report);                         // witness
        Assert.Matches(@"(?m)^\* \S+", report);                                  // the current branch, always kept
        Assert.Contains("more branch(es) not listed", report);
        Assert.Contains("150 files changed", report);                                  // the summary's total line
        Assert.Equal(report, AgentOrchestrator.CapForContext(report));
    }

    [Fact]
    public async Task AQuietRepository_IsReportedWhole_WithoutANote()
    {
        Repo(branches: 2, changedFiles: 3);

        var report = await RunAsync(new { });

        Assert.Contains("3 files changed", report);
        Assert.DoesNotContain("not listed", report);
    }
}
