using System.Diagnostics;
using System.IO;
using Inferpal.Localization;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Execution;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The cards of a run on a copy, as the Core gives them to every editor: the start card says what does not go with the copy
/// — or, for a copy already made, the commit it started from —, the review card's counted texts exist for every number of kept
/// changes, a change's place is its lines, and the card after an apply counts changes and groups what was not applied by
/// its reason — comparing and merging offered only where the person's own version is the reason.
/// </summary>
[Collection(WorkCopyCollection.Name)]
public sealed class CopyCardsTests
{
    /// <summary>A repository with one commit at <paramref name="repo"/>; <c>false</c> when git is not there.</summary>
    private static bool Committed(string repo)
    {
        foreach (var args in new[] { new[] { "init", "-q", "-b", "main", "." }, ["add", "-A"], ["commit", "-q", "-m", "start"] })
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-c", "user.email=test@example.com", "-c", "user.name=Test", "-c", "commit.gpgsign=false" }.Concat(args))
                psi.ArgumentList.Add(a);
            using var git = Process.Start(psi)!;
            git.StandardOutput.ReadToEnd();
            git.StandardError.ReadToEnd();
            if (!git.WaitForExit(30_000) || git.ExitCode != 0) return false;
        }
        return true;
    }

    private static CopyReviewFile Changed(string path, string before, string after, bool created = false) =>
        new(path, created, false, InlineDiffPlanner.Plan(before, after).Hunks);

    private static readonly string Lines = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line {i}")) + "\n";

    [Fact]
    public void TheStartCard_NamesWhatDoesNotGo_TheFirstEightAndACount()
    {
        var uncommitted = Enumerable.Range(1, 11).Select(i => $"f{i}.cs").ToList();

        var card = CopyCards.Start(new CopyStart(null, "a3f9c21", "main", uncommitted), held: null);

        Assert.Equal(Strings.CopyStartUncommitted(11), card.Uncommitted);
        Assert.Equal(CopyCards.UncommittedShown + 1, card.UncommittedFiles.Count);
        Assert.Equal("`f1.cs`", card.UncommittedFiles[0]);
        Assert.Equal(Strings.CopyStartMore(3), card.UncommittedFiles[^1]);
        Assert.Equal(Strings.CopyStartOf("main", "a3f9c21"), card.Rows[0].Value);
        Assert.Equal(Strings.CopyStartBranchNew, card.Rows[1].Value);
        Assert.Equal(Strings.CopyStartGo, card.Go);
    }

    [Fact]
    public void TheStartCard_WithNothingUncommitted_SaysNothing_AndOneGitCouldNotList_SaysSo()
    {
        var clean = CopyCards.Start(new CopyStart(null, "a3f9c21", null, []), held: null);
        var unknown = CopyCards.Start(new CopyStart(null, "a3f9c21", "main", null), held: null);

        Assert.Null(clean.Uncommitted);
        Assert.Empty(clean.UncommittedFiles);
        Assert.Equal(Strings.CopyStartOfDetached("a3f9c21"), clean.Rows[0].Value);                  // a detached HEAD
        Assert.Equal(Strings.CopyStartUncommittedUnknown, unknown.Uncommitted);                     // never read as none
    }

    [Fact]
    public async Task ForACopyAlreadyMade_TheStartCardNamesItsOwnCommit_AndNothingUncommittedSince()
    {
        var repo = Directory.CreateTempSubdirectory("inferpal-copycards-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(repo, "A.cs"), "class A { }\n");
            Assert.True(Committed(repo), "git is needed: this test is UNDECIDED, not green.");
            var (held, _) = await CopyRun.StartAsync(repo, CancellationToken.None);
            try
            {
                // The person committed, switched branch and left changes since: none of it is the copy's.
                var card = CopyCards.Start(new CopyStart(null, "fffffff", "feature", ["B.cs"]), held);

                Assert.Equal(Strings.CopyStartOfCommit(held!.StartCommit), card.Rows[0].Value);
                Assert.Equal(Strings.CopyStartBranchHeld(held.Copy.Branch), card.Rows[1].Value);
                Assert.Null(card.Uncommitted);
                Assert.Empty(card.UncommittedFiles);
                Assert.Equal(Strings.CopyStartGoHeld, card.Go);
            }
            finally { if (held is not null) await held.DiscardAsync(CancellationToken.None); }
        }
        finally { try { Directory.Delete(repo, recursive: true); } catch { } }
    }

    [Fact]
    public void TheReviewTexts_ExistForEveryNumberOfKeptChanges_ThePartialNoteOnlyBetweenNoneAndAll()
    {
        var after = Lines.Replace("line 3\n", "line three\n").Replace("line 20\n", "line twenty\n");
        var review = new CopyReview(1, "inferpal/1", [Changed("A.cs", Lines, after), Changed("B.cs", "", "new\n", created: true)], null);

        var texts = CopyCards.Review(review);

        Assert.Equal(4, texts.Counts.Count);                                                         // 0..3 changes kept
        Assert.Equal(Strings.CopyReviewCount(2, 3, 2), texts.Counts[2]);
        Assert.Equal(Strings.CopyReviewApply(3), texts.Apply[3]);
        Assert.Equal("", texts.Partial[0]);
        Assert.Equal(Strings.CopyReviewPartial(2, 3), texts.Partial[2]);
        Assert.Equal("", texts.Partial[3]);
        Assert.Null(texts.Nothing);
        Assert.Null(texts.LeftOut);
    }

    [Fact]
    public void AReviewOfNothing_SaysSo_AndOneThatCouldNotBeRead_SaysWhy_NeverNothing()
    {
        var nothing = CopyCards.Review(new CopyReview(1, "inferpal/1", [], null));
        var unread = CopyCards.Review(new CopyReview(0, "inferpal/1", [], "the copy's changes could not be read: denied"));

        Assert.Equal(Strings.CopyReviewNothing, nothing.Nothing);
        Assert.Null(unread.Nothing);
        Assert.Equal(Strings.CopyReviewLeftOut("the copy's changes could not be read: denied"), unread.LeftOut);
        Assert.Equal(Strings.CopyReviewDiscard, unread.Discard);                                        // the copy can still go
    }

    [Fact]
    public void AChangesKey_IsWhatItIs_NeverItsNumber()
    {
        var one = InlineDiffPlanner.Plan(Lines, Lines.Replace("line 20\n", "line twenty\n")).Hunks.Single();
        // The run went on: a change above it now comes first, and this one is numbered anew.
        var two = InlineDiffPlanner.Plan(Lines, Lines.Replace("line 3\n", "x\n").Replace("line 20\n", "line twenty\n")).Hunks;

        Assert.NotEqual(one.Index, two[1].Index);
        Assert.Equal(CopyCards.HunkKey("A.cs", one), CopyCards.HunkKey("A.cs", two[1]));
        Assert.NotEqual(CopyCards.HunkKey("A.cs", one), CopyCards.HunkKey("A.cs", two[0]));
        Assert.NotEqual(CopyCards.HunkKey("A.cs", one), CopyCards.HunkKey("B.cs", one));
        Assert.Equal(CopyCards.HunkKey("A.cs", two[1]), CopyCards.File(Changed("A.cs", Lines, Lines.Replace("line 3\n", "x\n")
                                                                       .Replace("line 20\n", "line twenty\n"))).HunkKeys[1]);
    }

    [Fact]
    public void TheNoticeOfAStart_SaysTheCopyLeftBehind_WithItsTwoGestures()
    {
        var copy = new WorkCopy { Repository = Path.GetTempPath(), Folder = Path.Combine(Path.GetTempPath(), "c", "3"), Branch = "inferpal/3", BaseCommit = "abc" };

        var counted = AwayNotice.Build([], "vscode", "VS Code", [], new HashSet<string>(), DateTime.Now, left: (copy, 3));
        var uncounted = AwayNotice.Build([], "vscode", "VS Code", [], new HashSet<string>(), DateTime.Now, left: (copy, null));

        Assert.Equal(Strings.AwayTitle, counted!.Title);
        Assert.Equal(Strings.AwayCopyLeft("VS Code", "inferpal/3", 3), counted.Copy!.Text);
        Assert.Equal(copy.Folder, counted.Copy.Folder);
        Assert.Equal((Strings.CopyReviewTheCopy, Strings.CopyReviewDiscard), (counted.Copy.Review, counted.Copy.Discard));
        Assert.Equal(Strings.AwayCopyLeftUncounted("VS Code", "inferpal/3"), uncounted!.Copy!.Text);
        Assert.Null(AwayNotice.Build([], "vscode", "VS Code", [], new HashSet<string>(), DateTime.Now));   // nothing to say
    }

    [Fact]
    public void TheCardOfACopy_IsNeverSavedWithTheConversation() =>
        Assert.Contains("copy", Inferpal.Services.Persistence.SessionManager.ScreenOnlyRoles);

    [Fact]
    public void AChangesPlace_IsItsLinesInTheNewText_OrTheLinesItRemoves()
    {
        var after = Lines.Replace("line 12\n", "line twelve\nline twelve and a half\n").Replace("line 25\n", "");

        var file = CopyCards.File(Changed("A.cs", Lines, after));
        var created = CopyCards.File(Changed("N.cs", "", "x\n", created: true));

        Assert.Equal([Strings.CopyReviewLines(12, 13), Strings.CopyReviewLine(25)], file.HunkLabels);    // the line it removes
        Assert.Null(file.Tag);
        Assert.Equal([Strings.CopyReviewFileKept(0, 2), Strings.CopyReviewFileKept(1, 2), Strings.CopyReviewFileKept(2, 2)], file.Kept);
        Assert.Equal(Strings.CopyReviewNewFile, created.Tag);
    }

    [Fact]
    public void TheAppliedCard_CountsChanges_AndGroupsWhatWasNotAppliedByItsReason()
    {
        var after = Lines.Replace("line 3\n", "line three\n").Replace("line 20\n", "line twenty\n");
        var review = new CopyReview(1, "inferpal/2",
            [Changed("A.cs", Lines, after), Changed("B.cs", Lines, after), Changed("C.cs", "", "c\n", created: true)], null);
        var kept = review.Files.ToDictionary(f => f.Path, f => (IReadOnlyCollection<int>)f.Hunks.Select(h => h.Index).ToList());
        var applied = new CopyApplied(["C.cs"],
            [new CopyLeftFile("A.cs", CopyLeftReason.ChangedSince), new CopyLeftFile("B.cs", CopyLeftReason.Unsaved)]);
        var copy = new WorkCopy { Repository = Path.GetTempPath(), Folder = Path.Combine(Path.GetTempPath(), "c", "2"), Branch = "inferpal/2", BaseCommit = "abc" };

        var workspace = Path.GetTempPath();

        var card = CopyCards.Applied(applied, kept, review, copy, "", workspace);

        Assert.Equal(Strings.CopyAppliedTitle(1, 5), card.Title);                                   // changes, never files
        Assert.Equal(Strings.CopyAppliedUndo, card.Undo);
        Assert.Equal(2, card.NotApplied.Count);
        var changed = card.NotApplied.Single(g => g.Note is not null);
        Assert.Equal(Strings.CopyNotApplied(2, Strings.CopyLeftChangedSince), changed.Title);
        Assert.Equal(Strings.CopyNotAppliedNote("inferpal/2"), changed.Note);
        var a = Assert.Single(changed.Files);
        Assert.True(a.CanMerge);
        Assert.Equal(Strings.CopyLineShort(3) + ", " + Strings.CopyLineShort(20), a.Lines);
        Assert.Equal(Path.GetFullPath(Path.Combine(copy.Folder, "A.cs")), a.CopyPath);
        Assert.Equal(Path.GetFullPath(Path.Combine(workspace, "A.cs")), a.MinePath);
        Assert.Equal(Strings.CopyMergePrompt("A.cs", "inferpal/2"), a.MergePrompt);
        var unsaved = card.NotApplied.Single(g => g.Note is null);                                   // saving is the remedy, not a merge
        Assert.Equal(Strings.CopyNotApplied(2, Strings.CopyLeftUnsaved), unsaved.Title);
        Assert.False(Assert.Single(unsaved.Files).CanMerge);
    }

    [Fact]
    public void InAnOpenedSubfolder_ThePersonsFileAndTheMergeQuestion_AreFoundFromThatFolder()
    {
        var review = new CopyReview(1, "inferpal/2", [Changed("src/Pay.cs", Lines, Lines.Replace("line 3\n", "x\n"))], null);
        var kept = new Dictionary<string, IReadOnlyCollection<int>> { ["src/Pay.cs"] = [0] };
        var copy = new WorkCopy { Repository = Path.GetTempPath(), Folder = Path.Combine(Path.GetTempPath(), "c", "2"), Branch = "inferpal/2", BaseCommit = "abc" };
        var workspace = Path.Combine(Path.GetTempPath(), "repo", "src");

        var card = CopyCards.Applied(new CopyApplied([], [new CopyLeftFile("src/Pay.cs", CopyLeftReason.ChangedSince)]), kept, review,
                                     copy, "src/", workspace);

        var file = Assert.Single(Assert.Single(card.NotApplied).Files);
        Assert.Equal(Path.GetFullPath(Path.Combine(workspace, "Pay.cs")), file.MinePath);           // never src/src/Pay.cs
        Assert.Equal(Path.GetFullPath(Path.Combine(copy.Folder, "src", "Pay.cs")), file.CopyPath);
        Assert.Equal(Strings.CopyMergePrompt("Pay.cs", "inferpal/2"), file.MergePrompt);
    }

    [Fact]
    public void AFileTheRunDeleted_OffersNoComparison_EvenWhenThePersonChangedIt()
    {
        var deleted = new CopyReviewFile("Old.cs", false, true, InlineDiffPlanner.Plan("a\nb\n", "").Hunks);
        var review = new CopyReview(1, "inferpal/2", [deleted], null);
        var kept = new Dictionary<string, IReadOnlyCollection<int>> { ["Old.cs"] = [.. deleted.Hunks.Select(h => h.Index)] };
        var copy = new WorkCopy { Repository = Path.GetTempPath(), Folder = Path.GetTempPath(), Branch = "inferpal/2", BaseCommit = "abc" };

        var card = CopyCards.Applied(new CopyApplied([], [new CopyLeftFile("Old.cs", CopyLeftReason.ChangedSince)]), kept, review,
                                     copy, "", Path.GetTempPath());

        Assert.False(Assert.Single(Assert.Single(card.NotApplied).Files).CanMerge);                  // no copy to compare with
    }

    [Fact]
    public void AnApplyThatWroteNothing_OffersNoUndo()
    {
        var review = new CopyReview(1, "inferpal/2", [Changed("A.cs", Lines, Lines.Replace("line 3\n", "x\n"))], null);
        var kept = new Dictionary<string, IReadOnlyCollection<int>> { ["A.cs"] = [0] };
        var copy = new WorkCopy { Repository = Path.GetTempPath(), Folder = Path.GetTempPath(), Branch = "inferpal/2", BaseCommit = "abc" };

        var card = CopyCards.Applied(new CopyApplied([], [new CopyLeftFile("A.cs", CopyLeftReason.DeletedSince)]), kept, review, copy,
                                     "", Path.GetTempPath());

        Assert.Equal(Strings.CopyAppliedTitle(0, 1), card.Title);
        Assert.Null(card.Undo);
    }
}
