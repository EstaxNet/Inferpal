using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Inferpal.Host;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Models;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A run on a copy through the host's protocol (pari 106), as VS Code and ACP drive it: the start card from
/// <c>copy/prepare</c>, a question sent with <c>inCopy</c> whose turn writes in the copy and comes back with its review, the
/// kept hunks written by <c>copy/apply</c>, the copy removed by <c>copy/discard</c> — the person's file untouched until the
/// apply.
/// </summary>
public partial class HostServerTests
{
    private static bool Git(string repo, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-c", "user.email=test@example.com", "-c", "user.name=Test", "-c", "commit.gpgsign=false" }.Concat(args))
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        return p.WaitForExit(30_000) && p.ExitCode == 0;
    }

    /// <summary>The harness's folder as a repository with one commit (Pay.cs), initialized; cards answered "Once".</summary>
    private static async Task CommittedAsync(Harness h)
    {
        File.WriteAllText(Path.Combine(h.RootDir, "Pay.cs"), "class Pay\n{\n    int A() => 1;\n}\n");
        Assert.True(Git(h.RootDir, "init", "-q", ".") && Git(h.RootDir, "add", "-A") && Git(h.RootDir, "commit", "-q", "-m", "start"),
                    "git is needed: this test is UNDECIDED, not green.");
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        h.Target.ApprovalAnswer = 1;   // Once
    }

    private static Task<T> CopyCall<T>(Harness h, string method, object? args = null) =>
        (args is null ? h.Client.InvokeAsync<T>(method) : h.Client.InvokeWithParameterObjectAsync<T>(method, args))
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

    [Fact]
    public async Task Copy_AQuestionForACopy_WritesInTheCopy_ComesBackWithItsReview_AndOnlyTheApplyWritesThePersonsFile()
    {
        using var h = CreateHarness();
        await CommittedAsync(h);
        var pay = Path.Combine(h.RootDir, "Pay.cs");
        File.WriteAllText(Path.Combine(h.RootDir, "Notes.txt"), "mine\n");      // not committed
        var card = await CopyCall<CopyPrepareResult>(h, "copy/prepare");
        Assert.Null(card.Refusal);
        Assert.Contains("Notes.txt", card.Uncommitted!);
        Assert.False(card.Held);
        Assert.Equal(Strings.CopyStartTitle, card.Card!.Title);                                       // the start card's texts
        Assert.Contains("`Notes.txt`", card.Card.UncommittedFiles);

        string? root = null;
        h.Fake.OnAgentRun = async (history, tools, _, ct) =>
        {
            root = RunRoot.Copy;
            // Read first: a run never replaces a file it has not read.
            await tools.ExecuteAsync("read_file", JsonSerializer.SerializeToElement(new { path = "Pay.cs" }), ct);
            await tools.ExecuteAsync("write_file",
                JsonSerializer.SerializeToElement(new { path = "Pay.cs", content = "class Pay\n{\n    int A() => 2;\n}\n" }), ct);
            return new AgentResult("Changed A.", [], history);
        };
        var sent = await CopyCall<ChatSendResult>(h, "chat/send", new { prompt = "Make A return 2", agentMode = false, inCopy = true });

        try
        {
            Assert.Null(sent.Error);
            Assert.NotNull(root);
            Assert.False(PathComparer.SameDirectory(root, h.RootDir));                                // the turn ran in the copy
            var file = Assert.Single(sent.Copy!.Files);
            Assert.Equal("Pay.cs", file.Path);
            Assert.Equal(Strings.CopyReviewApply(1), sent.Copy.Texts!.Apply[1]);                     // the review card's texts
            Assert.Equal(Strings.CopyReviewFileKept(0, 1), file.Texts!.Kept[0]);
            Assert.Contains("    int A() => 2;", Assert.Single(file.Hunks).Added);
            Assert.Contains("int A() => 1;", File.ReadAllText(pay));                                 // the person's file as it was
            Assert.True((await CopyCall<CopyPrepareResult>(h, "copy/prepare")).Held);

            var applied = await CopyCall<CopyApplyResult>(h, "copy/apply",
                new { reviewId = sent.Copy.Id, kept = new Dictionary<string, List<int>> { ["Pay.cs"] = [file.Hunks[0].Index] } });

            Assert.Null(applied.Error);
            Assert.Equal(["Pay.cs"], applied.Written);
            Assert.Equal(Strings.CopyAppliedTitle(1, 1), applied.Card!.Title);
            Assert.False(string.IsNullOrEmpty(applied.RunId));
            Assert.Contains("int A() => 2;", File.ReadAllText(pay));
        }
        finally
        {
            var discarded = await CopyCall<CopyDiscardResult>(h, "copy/discard");
            Assert.Null(discarded.Problem);
            Assert.Null(discarded.Error);
            Assert.Matches("^" + System.Text.RegularExpressions.Regex.Escape(Strings.CopyDiscarded("BRANCH"))
                               .Replace("BRANCH", System.Text.RegularExpressions.Regex.Escape(WorkCopy.BranchPrefix) + @"\d+") + "$",
                           discarded.Message);
        }
        Assert.False((await CopyCall<CopyPrepareResult>(h, "copy/prepare")).Held);
        Assert.Equal(Strings.CopyNoneOpen, (await CopyCall<CopyDiscardResult>(h, "copy/discard")).Error);
    }

    [Fact]
    public async Task Copy_AFollowUpForTheCopy_GoesToTheCopyItHolds_AnOrdinaryQuestionDoesNot()
    {
        using var h = CreateHarness();
        await CommittedAsync(h);
        var roots = new List<string?>();
        h.Fake.OnAgentRun = (history, _, _, _) =>
        {
            roots.Add(RunRoot.Copy);
            return Task.FromResult(new AgentResult("ok", [], history));
        };

        try
        {
            await CopyCall<ChatSendResult>(h, "chat/send", new { prompt = "first", agentMode = false, inCopy = true });
            await CopyCall<ChatSendResult>(h, "chat/send", new { prompt = "then", agentMode = false, inCopy = true });
            await CopyCall<ChatSendResult>(h, "chat/send", new { prompt = "here", agentMode = false });
        }
        finally { await CopyCall<CopyDiscardResult>(h, "copy/discard"); }

        Assert.Equal(3, roots.Count);
        Assert.NotNull(roots[0]);
        Assert.Equal(roots[0], roots[1]);                                                             // one copy, held
        Assert.Null(roots[2]);                                                                        // an ordinary question
    }

    [Fact]
    public async Task Copy_ACopyHeldWhenTheHostStopped_IsTakenUpAndSaid_ByTheNextSession()
    {
        var repo = Directory.CreateTempSubdirectory("inferpal-hostcopy-repo-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(repo, "Pay.cs"), "class Pay { }\n");
            Assert.True(Git(repo, "init", "-q", ".") && Git(repo, "add", "-A") && Git(repo, "commit", "-q", "-m", "start"),
                        "git is needed: this test is UNDECIDED, not green.");
            string? branch, folder = null;
            using (var first = CreateHarness())
            {
                await first.InitializeAsync(rootDir: repo).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
                first.Fake.OnAgentRun = (history, _, _, _) =>
                {
                    folder = RunRoot.Copy;
                    return Task.FromResult(new AgentResult("ok", [], history));
                };
                await CopyCall<ChatSendResult>(first, "chat/send", new { prompt = "go", agentMode = false, inCopy = true });
                branch = (await CopyCall<CopyPrepareResult>(first, "copy/prepare")).Held ? "held" : null;
            }   // the host stops with the copy held: nothing discards it
            // What the editor closing leaves: the holder gone (here the same test process lives on, so it is said).
            var record = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(folder + ".json"))!;
            record["OwnerPid"] = 99_999_999;
            File.WriteAllText(folder + ".json", record.ToJsonString());

            using var next = CreateHarness();
            await next.InitializeAsync(rootDir: repo).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            var card = await CopyCall<CopyPrepareResult>(next, "copy/prepare");

            Assert.Equal("held", branch);
            Assert.True(card.Held);
            Assert.StartsWith(WorkCopy.BranchPrefix, card.LeftBefore);
            Assert.Null((await CopyCall<CopyDiscardResult>(next, "copy/discard")).Problem);
            Assert.False((await CopyCall<CopyPrepareResult>(next, "copy/prepare")).Held);
        }
        finally
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(repo, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(repo, recursive: true);
            }
            catch { }
        }
    }

    [Fact]
    public async Task Copy_ACopyAnotherSessionLeft_IsNeverReachedByAnApplyOrADiscard_OnlyTakenUpByTheCard()
    {
        using var h = CreateHarness();
        await CommittedAsync(h);
        var (left, _) = await CopyRun.StartAsync(h.RootDir, CancellationToken.None);
        CopyRunTests.LeftByAGoneProcess(left!.Copy);

        try
        {
            var applied = await CopyCall<CopyApplyResult>(h, "copy/apply", new { reviewId = 1, kept = new Dictionary<string, List<int>>() });
            var discarded = await CopyCall<CopyDiscardResult>(h, "copy/discard");

            Assert.Equal(Strings.CopyNoneOpen, applied.Error);
            Assert.Equal(Strings.CopyNoneOpen, discarded.Error);
            Assert.True(Directory.Exists(left.Copy.Folder));                                          // never reached

            var card = await CopyCall<CopyPrepareResult>(h, "copy/prepare");

            Assert.True(card.Held);
            Assert.Equal(left.Copy.Branch, card.LeftBefore);
            Assert.Equal(left.Copy.BaseCommit[..7], card.Commit);
        }
        finally { await CopyCall<CopyDiscardResult>(h, "copy/discard"); }
        Assert.False(Directory.Exists(left.Copy.Folder));
    }

    /// <summary>`/run-copy` in an editor opens the start card with the question; nothing is made before the person answers
    /// it. Without a question, its usage; without an editor, a deterministic message — never the raw command to the model.</summary>
    [Fact]
    public async Task Slash_RunCopy_OpensTheStartCard_WithTheQuestion_AndMakesNothing()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var asked = await CopyCall<Host.SlashCommandResult>(h, "command/slash", new { text = "/run-copy Rework  the payment module" });
        var bare = await CopyCall<Host.SlashCommandResult>(h, "command/slash", new { text = "/run-copy" });

        using var headless = CreateHarness();
        await headless.Client.InvokeWithParameterObjectAsync<InitializeResult>(
            "initialize", new { rootDir = headless.RootDir, editorSurface = false }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var withoutEditor = await CopyCall<Host.SlashCommandResult>(headless, "command/slash", new { text = "/run-copy Rework it" });

        var effect = Assert.Single(asked.Effects!);
        Assert.Equal("runCopy", effect.Kind);
        Assert.Equal("Rework  the payment module", effect.Value);                                    // as typed
        // Alone, it shows the copy held: with none held or left, it says so and how to start one.
        Assert.Equal(Strings.CopyNoneOpen + " " + Strings.SlashUsage("/run-copy <question>"), bare.Markdown);
        Assert.Equal(Strings.RunCopyNotHereYet, withoutEditor.Markdown);
        Assert.False(Directory.Exists(Path.Combine(WorkCopy.Home, FileHistoryService.FolderNameOf(Path.GetFullPath(h.RootDir)))));
    }

    [Fact]
    public async Task Copy_AnApplyWithoutItsHunks_IsToldWhatIsMissing()
    {
        using var h = CreateHarness();
        await CommittedAsync(h);

        var applied = await CopyCall<CopyApplyResult>(h, "copy/apply", new { });

        Assert.Contains("'kept' is required", applied.Error);
        Assert.Empty(applied.Written);
    }

    [Fact]
    public async Task Copy_AFolderWithoutACommit_RefusesTheCopy_AndSaysWhy()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var card = await CopyCall<CopyPrepareResult>(h, "copy/prepare");
        var sent = await CopyCall<ChatSendResult>(h, "chat/send", new { prompt = "go", agentMode = false, inCopy = true });

        Assert.Contains("a copy starts from the last commit", card.Refusal);
        Assert.Equal(Strings.CopyNotMade("this folder is not a git repository with a commit: a copy starts from the last commit"), sent.Error);
        Assert.Equal(Strings.CopyNotMade(card.Refusal!), card.RefusalText);
        Assert.Null(card.Card);
        Assert.Null(sent.Copy);
        Assert.False(sent.QuestionKept);                       // never in the history: Regenerate takes back nothing before it
    }
}
