using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A run on a copy from its start card to the person's choice (pari 106): what the card says before anything is made, the
/// run entered in the copy, its changes reviewed file by file and hunk by hunk — only the folder the person opened, no
/// binary file as text —, the kept hunks of the review they saw applied in a run of their history, an unsaved buffer left,
/// the copy discarded once and never while a question is answered in it, and a copy an earlier session left found again
/// only once its holder is gone.
/// </summary>
[Collection(WorkCopyCollection.Name)]
public sealed class CopyRunTests : IDisposable
{
    private readonly DirectoryInfo _repo = Directory.CreateTempSubdirectory("inferpal-copyrun-repo-");

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_repo.FullName, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            _repo.Delete(recursive: true);
        }
        catch { }
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
        Assert.True(Git("init", "-q", "-b", "main", ".") && Git("add", "-A") && Git("commit", "-q", "-m", "start"),
                    "git is needed: this test is UNDECIDED, not green.");
    }

    /// <summary>Makes <paramref name="copy"/> look left by a process that is gone: what the editor closing leaves.</summary>
    internal static void LeftByAGoneProcess(WorkCopy copy)
    {
        var record = copy.Folder + ".json";
        var json = JsonNode.Parse(File.ReadAllText(record))!;
        json["OwnerPid"] = 99_999_999;
        File.WriteAllText(record, json.ToJsonString());
    }

    private static Dictionary<string, IReadOnlyCollection<int>> Keep(string path, params int[] hunks) => new() { [path] = hunks };

    [Fact]
    public async Task TheStartCard_SaysTheCommit_TheBranch_AndWhatDoesNotGo()
    {
        Commit();
        File.WriteAllText(Repo("Refunds.cs"), "class Refunds { }\n");      // not committed

        var start = await CopyRun.PrepareAsync(_repo.FullName, CancellationToken.None);

        Assert.Null(start.Refusal);
        Assert.False(string.IsNullOrEmpty(start.Commit));
        Assert.Equal("main", start.Branch);
        Assert.Equal(["Refunds.cs"], start.Uncommitted);
        Assert.False(Directory.Exists(Path.Combine(WorkCopy.Home, FileHistoryService.FolderNameOf(Path.GetFullPath(_repo.FullName)))));   // nothing made
    }

    [Fact]
    public async Task AFolderWithoutACommit_IsRefusedOnTheCard()
    {
        var start = await CopyRun.PrepareAsync(_repo.FullName, CancellationToken.None);

        Assert.Contains("a copy starts from the last commit", start.Refusal);
    }

    [Fact]
    public async Task TheRunWorksInTheCopy_ItsReviewListsEachFileAndHunk_TheKeptOnesAreApplied_ThenTheCopyGoes()
    {
        Commit();
        var (run, refusal) = await CopyRun.StartAsync(_repo.FullName, CancellationToken.None);
        Assert.True(run is not null, refusal);
        using (run!.Enter())
        {
            Assert.Equal(Path.GetFullPath(run.Copy.Folder), RunRoot.Copy);
            File.WriteAllText(Path.Combine(RunRoot.Copy!, "Pay.cs"), "class Pay\n{\n    int A() => 10;\n    int B() => 2;\n    int C() => 3;\n}\n");
            File.WriteAllText(Path.Combine(RunRoot.Copy!, "Gateway.cs"), "class Gateway { }\n");
            File.Delete(Path.Combine(RunRoot.Copy!, "Old.cs"));
        }
        Assert.Null(RunRoot.Copy);

        var review = await run.ReviewAsync(CancellationToken.None);

        Assert.Null(review.Problem);
        Assert.Equal(run.Copy.Branch, review.Branch);
        var pay = review.Files.Single(f => f.Path == "Pay.cs");
        Assert.Equal(2, pay.Hunks.Count);
        Assert.True(review.Files.Single(f => f.Path == "Gateway.cs").Created);
        Assert.True(review.Files.Single(f => f.Path == "Old.cs").Deleted);

        var history = new FileHistoryService();
        var outcome = await run.ApplyAsync(review.Id, Keep("Pay.cs", pay.Hunks[1].Index), history, CancellationToken.None);

        Assert.Null(outcome.Refusal);
        Assert.Equal(["Pay.cs"], outcome.Applied!.Written);
        var paid = File.ReadAllText(Repo("Pay.cs")).Replace("\r\n", "\n");
        Assert.Contains("int A() => 1;", paid);                                                     // dropped
        Assert.Contains("int C() => 3;", paid);                                                     // kept
        Assert.False(File.Exists(Repo("Gateway.cs")));                                              // not kept
        Assert.Null(history.CurrentRunId);                                                          // its own run, closed
        Assert.False(string.IsNullOrEmpty(outcome.RunId));

        Assert.Equal((true, (string?)null), await run.DiscardAsync(CancellationToken.None));
        Assert.True(run.Discarded);
        Assert.False(Directory.Exists(run.Copy.Folder));
        Assert.Equal((true, (string?)null), await run.DiscardAsync(CancellationToken.None));        // once
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            run.ApplyAsync(review.Id, Keep("Pay.cs"), history, CancellationToken.None));
    }

    [Fact]
    public async Task AnApply_OfAReviewTheCopyWasReadAgainSince_IsRefused()
    {
        Commit();
        var (run, _) = await CopyRun.StartAsync(_repo.FullName, CancellationToken.None);
        File.WriteAllText(Path.Combine(run!.Copy.Folder, "Pay.cs"), "class Pay { }\n");
        var seen = await run.ReviewAsync(CancellationToken.None);
        await run.ReviewAsync(CancellationToken.None);                                              // read again (a follow-up)

        var outcome = await run.ApplyAsync(seen.Id, Keep("Pay.cs", 0), new FileHistoryService(), CancellationToken.None);

        Assert.Equal(Strings.CopyReviewChanged, outcome.Refusal);
        Assert.Null(outcome.Applied);
        Assert.Contains("int A() => 1;", File.ReadAllText(Repo("Pay.cs")));
        await run.DiscardAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AFileWithAnUnsavedBuffer_IsLeftAndNamed_AKeptPathTheReviewDidNotShowIsNamed()
    {
        Commit();
        var (run, _) = await CopyRun.StartAsync(_repo.FullName, CancellationToken.None);
        File.WriteAllText(Path.Combine(run!.Copy.Folder, "Pay.cs"), "class Pay { }\n");
        var review = await run.ReviewAsync(CancellationToken.None);
        var kept = new Dictionary<string, IReadOnlyCollection<int>> { ["Pay.cs"] = [0], ["Ghost.cs"] = [0] };

        var outcome = await run.ApplyAsync(review.Id, kept, new FileHistoryService(), CancellationToken.None,
                                           unsaved: path => Path.GetFileName(path) == "Pay.cs");

        Assert.Empty(outcome.Applied!.Written);
        Assert.Equal(CopyLeftReason.Unsaved, outcome.Applied.Left.Single(l => l.Path == "Pay.cs").Reason);
        Assert.Equal(CopyLeftReason.NotInReview, outcome.Applied.Left.Single(l => l.Path == "Ghost.cs").Reason);
        Assert.Contains("int A() => 1;", File.ReadAllText(Repo("Pay.cs")));
        await run.DiscardAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ABinaryFile_AndAFileOutsideTheOpenedFolder_AreSaid_NeverOffered()
    {
        Directory.CreateDirectory(Repo("app"));
        File.WriteAllText(Repo(Path.Combine("app", "Main.cs")), "class Main { }\n");
        Commit();
        var (run, _) = await CopyRun.StartAsync(Repo("app"), CancellationToken.None);
        File.WriteAllText(Path.Combine(run!.Copy.Folder, "app", "Main.cs"), "class Main { int X; }\n");
        File.WriteAllBytes(Path.Combine(run.Copy.Folder, "app", "logo.bin"), [0x89, 0x50, 0x4E, 0x47, 0x00, 0x00, 0x01, 0x02]);
        File.WriteAllText(Path.Combine(run.Copy.Folder, "Pay.cs"), "class Pay { }\n");               // outside app/

        var review = await run.ReviewAsync(CancellationToken.None);

        Assert.Equal(["app/Main.cs"], review.Files.Select(f => f.Path));
        Assert.Contains("binary", review.Problem);
        Assert.Contains("app/logo.bin", review.Problem);
        Assert.Contains("outside the folder you opened", review.Problem);
        Assert.Contains("Pay.cs", review.Problem);
        await run.DiscardAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AFileChangedWithoutALineToKeep_IsSaid_NeverOfferedWithNothingToTick()
    {
        Commit();
        var (run, _) = await CopyRun.StartAsync(_repo.FullName, CancellationToken.None);
        File.WriteAllText(Path.Combine(run!.Copy.Folder, "__init__.py"), "");                        // an empty file created
        File.WriteAllText(Path.Combine(run.Copy.Folder, "Pay.cs"), "class Pay { }\n");

        var review = await run.ReviewAsync(CancellationToken.None);

        Assert.Equal(["Pay.cs"], review.Files.Select(f => f.Path));
        Assert.Contains("without a line to keep or drop", review.Problem);
        Assert.Contains("__init__.py", review.Problem);
        await run.DiscardAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WithNoFolderOpen_NoCopyIsMade_NorLookedFor()
    {
        var start = await CopyRun.PrepareAsync("", CancellationToken.None);
        var (made, refusal) = await CopyRun.StartAsync("", CancellationToken.None);

        Assert.Contains("no folder is open", start.Refusal);
        Assert.Null(made);
        Assert.Contains("no folder is open", refusal);
        Assert.Null(await CopyRun.FindLeftAsync("", CancellationToken.None));
    }

    [Theory]
    [InlineData("/run-copy Rework it", "Rework it")]
    [InlineData("/run-copy rewrite:\n    if (x)\n        y();", "rewrite:\n    if (x)\n        y();")]
    [InlineData("/run-copy\nRework  it", "Rework  it")]
    [InlineData("/run-copy", "")]
    [InlineData("  /run-copy   ", "")]
    public void TheQuestion_IsKeptAsTyped(string typed, string question) =>
        Assert.Equal(question, Inferpal.Services.Commands.RunCopyCommandHandler.Question(typed));

    [Theory]
    [InlineData("/run-copy Rework it", true)]
    [InlineData("/RUN-COPY\nRework it", true)]
    [InlineData("/run-copy", false)]
    [InlineData("/run-copyist Rework", false)]
    [InlineData("Rework it", false)]
    public void ACopyQuestion_IsKnownByItsCommand(string text, bool copy) =>
        Assert.Equal(copy, Inferpal.Services.Commands.RunCopyCommandHandler.IsCopyQuestion(text));

    [Fact]
    public async Task ACopyAQuestionIsAnsweredIn_IsNeverDiscarded()
    {
        Commit();
        var (run, _) = await CopyRun.StartAsync(_repo.FullName, CancellationToken.None);
        (bool Done, string? Problem) refused;
        using (run!.Use())
            refused = await run.DiscardAsync(CancellationToken.None);

        Assert.False(refused.Done);
        Assert.Equal(Strings.CopyInUse, refused.Problem);
        Assert.True(Directory.Exists(run.Copy.Folder));
        Assert.True((await run.DiscardAsync(CancellationToken.None)).Done);                       // reference arm
    }

    [Fact]
    public async Task ACopyIsFoundAgain_OnlyOnceItsHolderIsGone_ThenUntilItIsDiscarded()
    {
        Commit();
        var (run, _) = await CopyRun.StartAsync(_repo.FullName, CancellationToken.None);
        Assert.False(run!.LeftBefore);

        // Its holder lives (this process: another editor, another ACP thread): not another session's to take.
        Assert.Null(await CopyRun.FindLeftAsync(_repo.FullName, CancellationToken.None));
        LeftByAGoneProcess(run.Copy);
        var found = await CopyRun.FindLeftAsync(_repo.FullName, CancellationToken.None);

        Assert.NotNull(found);
        Assert.True(found!.LeftBefore);
        Assert.Equal(run.Copy.Branch, found.Copy.Branch);
        Assert.True(PathComparer.SameDirectory(run.Copy.Folder, found.Copy.Folder));
        Assert.Equal(run.Copy.BaseCommit, found.Copy.BaseCommit);
        Assert.Null(await CopyRun.FindLeftAsync(_repo.FullName, CancellationToken.None));            // taken up: held again
        Assert.Equal((true, (string?)null), await found.DiscardAsync(CancellationToken.None));
        Assert.False(File.Exists(run.Copy.Folder + ".json"));                                       // gone with its record
    }

    [Fact]
    public async Task EveryRunInOneCopy_FilesItsAgreementsUnderOneScope_ThatNoOtherCopyShares()
    {
        Commit();
        var (first, _) = await CopyRun.StartAsync(_repo.FullName, CancellationToken.None);
        var (second, _) = await CopyRun.StartAsync(_repo.FullName, CancellationToken.None);
        string? a, b, c;
        using (first!.Enter()) a = RunRoot.GrantsScope;
        using (first.Enter()) b = RunRoot.GrantsScope;
        using (second!.Enter()) c = RunRoot.GrantsScope;

        Assert.NotNull(a);
        Assert.Equal(a, b);                                                                          // a follow-up keeps it
        Assert.NotEqual(a, c);
        await first.DiscardAsync(CancellationToken.None);
        await second.DiscardAsync(CancellationToken.None);
    }
}
