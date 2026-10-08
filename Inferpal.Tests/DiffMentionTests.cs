using System.Diagnostics;
using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Editor;
using Inferpal.Services.Presentation;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>@diff</c> attaches a diff, or says why there is none — never a chip that holds no diff — and a chip holding part of
/// the diff says so to the person.
/// </summary>
/// <remarks>
/// ⚠ Both editors attached whatever <c>get_git_status</c> answered: outside a repository, with git refusing, or with no
/// change, the report still made a "📊 @diff" chip, and the question went to the model as if it carried a diff — while
/// docs/mentions.md promises a notice whenever a mention has nothing to attach. And the diff is cut to one tool result
/// (a few thousand characters): the note saying so is written for the model, the chip said nothing to the person.
/// </remarks>
public sealed class DiffMentionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-diff-mention-").FullName;

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

    private void CommittedRepository(string content)
    {
        Assert.True(Git("init -q ."), "`git init` failed: UNDECIDED, not green.");
        File.WriteAllText(Path.Combine(_root, "a.txt"), content);
        Assert.True(Git("add a.txt"), "`git add` failed: UNDECIDED, not green.");
        Assert.True(Git("-c user.email=t@t -c user.name=t commit -q -m one"), "`git commit` failed: UNDECIDED, not green.");
    }

    private Task<GetGitStatusTool.Report> ReportAsync() =>
        new GetGitStatusTool(new NullEditorSurface(), () => _root).ReportAsync(null, includeDiff: true, diffPath: null, CancellationToken.None);

    [Fact]
    public async Task OutsideARepository_ItIsANotice_NotAChip()
    {
        var report = await ReportAsync();

        Assert.Equal(GetGitStatusTool.State.NotARepository, report.Outcome);
        var mention = MentionController.DiffMention(report, "📊 @diff");
        Assert.Null(mention.Label);
        Assert.Equal(Strings.MentionDiffUnavailable(Strings.GitNotRepo), mention.Notice);
    }

    [Fact]
    public async Task WithNoChange_ItIsANotice_NotAChip()
    {
        CommittedRepository("hello\n");

        var report = await ReportAsync();

        Assert.Equal(GetGitStatusTool.State.Clean, report.Outcome);
        var mention = MentionController.DiffMention(report, "📊 @diff");
        Assert.Null(mention.Content);
        Assert.Equal(Strings.MentionDiffEmpty, mention.Notice);
    }

    [Fact]
    public async Task AChange_IsAttached_UnderItsPlainLabel()
    {
        // Reference arm: a diff that fits is attached whole, under the label the editor gave it.
        CommittedRepository("hello\n");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "hello\nchanged\n");

        var report = await ReportAsync();

        Assert.Equal(GetGitStatusTool.State.Changes, report.Outcome);
        Assert.False(report.DiffCut);
        var mention = MentionController.DiffMention(report, "📊 @diff");
        Assert.Null(mention.Notice);
        Assert.Equal("📊 @diff", mention.Label);
        Assert.Contains("+changed", mention.Content);
    }

    [Fact]
    public async Task ADiffCutToTheToolsBudget_SaysSoOnTheChip()
    {
        CommittedRepository("hello\n");
        var lines = Enumerable.Range(0, 2_000).Select(i => $"line {i} of a long change that runs past the budget");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "hello\n" + string.Join("\n", lines) + "\n");

        var report = await ReportAsync();

        Assert.Equal(GetGitStatusTool.State.Changes, report.Outcome);
        Assert.True(report.DiffTotal > 50_000, $"the diff should be long: {report.DiffTotal}");
        Assert.True(report.DiffCut, "a diff over the budget is held in part");
        var mention = MentionController.DiffMention(report, "📊 @diff");
        Assert.Equal(Strings.MentionDiffCutLabel("📊 @diff", report.DiffShown, report.DiffTotal), mention.Label);
    }

    [Fact]
    public void BothEditors_DecideTheDiffMentionThroughTheSamePresenter()
    {
        var root = ConversationPersistenceSilenceTests.RepoRoot();
        var vm   = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalToolWindowData.Mentions.cs"));
        var host = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal.Host", "HostSlashCommands.cs"));

        foreach (var (name, code) in new[] { ("Visual Studio", vm), ("VS Code host", host) })
        {
            Assert.Contains("MentionController.DiffMention(report,", code);
            // Witness and rule: the tool's TEXT is no longer what the mention attaches.
            Assert.DoesNotContain("\"get_git_status\", MentionArgs(", code);
            Assert.DoesNotContain("\"get_git_status\", JsonSerializer.SerializeToElement(new { include_diff = true })", code);
            Assert.True(code.Contains("GitStatus.ReportAsync(", StringComparison.Ordinal), $"{name} no longer reads the report");
        }
    }
}
