using System.IO;
using System.Security.Cryptography;
using System.Text;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Inferpal.Services.Execution;
using Inferpal.Services.Presentation;
using Xunit;
using static Inferpal.Services.Commands.UndoRunCommandHandler;

namespace Inferpal.Tests;

/// <summary>
/// A run reviewed file by file: each file undone, redone or kept on its own; a file changed after the run is never
/// overwritten by the state before it — not by its own Undo, not by Undo run.
/// </summary>
/// <remarks>⚠ Undo run put back the state before the run over every file it had changed, including one the person had
/// edited since: what they wrote after the run was lost, saved aside but never said.</remarks>
[Collection(CultureSerialCollection.Name)]
public sealed class RunReviewTests : IDisposable
{
    private readonly string _dir  = Directory.CreateTempSubdirectory("inferpal-run-review").FullName;
    private readonly string _user = Directory.CreateTempSubdirectory("inferpal-run-review-key").FullName;

    public void Dispose()
    {
        Strings.ApplyLanguage(null);
        try { Directory.Delete(_dir, recursive: true); } catch { /* cleanup */ }
        try { Directory.Delete(_user, recursive: true); } catch { /* cleanup */ }
    }

    private FileHistoryService Service() => new() { WorkspaceRoot = () => _dir, UserHistoryRoot = _user };

    private string File(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    private static string Text(string path) => System.IO.File.ReadAllText(path);

    private static async Task Edit(FileHistoryService history, string path, string content)
    {
        var (saved, _) = await history.BackUpBeforeChangeAsync(path, CancellationToken.None);
        Assert.True(saved);
        await System.IO.File.WriteAllTextAsync(path, content);
    }

    /// <summary>A closed run that edited A.cs and B.cs and created New.cs.</summary>
    private async Task<(FileHistoryService History, HistoryRun Run, string A, string B, string Created)> ARun()
    {
        var a = File("A.cs", "a before");
        var b = File("B.cs", "b before");
        var created = Path.Combine(_dir, "New.cs");
        var history = Service();
        history.BeginRun();
        await Edit(history, a, "a by the run");
        await Edit(history, b, "b by the run");
        await Edit(history, created, "created by the run");
        history.EndRun();
        return (history, Assert.Single(history.Runs), a, b, created);
    }

    private Task<(string Message, RunReviewModel? Review)> Act(FileHistoryService history, HistoryRun run, string path,
                                                                RunFileAction action) =>
        FileActionAsync(history, run.Id, path, action, _dir, busy: false, CancellationToken.None);

    [Fact]
    public async Task OneFileUndone_IsBackAsBeforeTheRun_TheOthersStayAsTheRunLeftThem()
    {
        var (history, run, a, b, created) = await ARun();

        var (message, review) = await Act(history, run, a, RunFileAction.Undo);

        Assert.Equal("a before", Text(a));
        Assert.Equal("b by the run", Text(b));
        Assert.True(System.IO.File.Exists(created));
        Assert.Equal(Strings.RunFileRestored("A.cs"), message);
        Assert.Equal(RunFileState.Undone, review!.Files.Single(f => f.Path == a).State);
        Assert.Equal(RunFileState.Pending, review.Files.Single(f => f.Path == b).State);
    }

    [Fact]
    public async Task AFileChangedAfterTheRun_IsNotUndone_AndItsChangeStays()
    {
        var (history, run, a, _, _) = await ARun();
        await System.IO.File.WriteAllTextAsync(a, "a edited by hand after the run");

        var (message, review) = await Act(history, run, a, RunFileAction.Undo);

        Assert.Equal("a edited by hand after the run", Text(a));
        Assert.Equal(Strings.RunFileChangedSince("A.cs"), message);
        Assert.Equal(RunFileState.ChangedSince, review!.Files.Single(f => f.Path == a).State);
    }

    [Fact]
    public async Task UndoRun_LeavesWhatChangedSinceAndWhatWasKept_AndNamesThem()
    {
        var (history, run, a, b, created) = await ARun();
        await System.IO.File.WriteAllTextAsync(a, "a edited by hand after the run");
        await Act(history, run, b, RunFileAction.Keep);

        var answer = await UndoRunCommandHandler.HandleAsync(history, ["/undo-run"], _dir, CancellationToken.None);

        Assert.Equal("a edited by hand after the run", Text(a));
        Assert.Equal("b by the run", Text(b));
        Assert.False(System.IO.File.Exists(created));   // WITNESS: the undo did run, on what it could give back
        Assert.Contains(Strings.UndoRunLeftChanged("A.cs"), answer, StringComparison.Ordinal);
        Assert.Contains(Strings.UndoRunLeftKept("B.cs"), answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARedo_GivesTheRunsVersionBack()
    {
        var (history, run, a, _, _) = await ARun();
        await Act(history, run, a, RunFileAction.Undo);

        var (message, review) = await Act(history, run, a, RunFileAction.Redo);

        Assert.Equal("a by the run", Text(a));
        Assert.Equal(Strings.RunFileRedone("A.cs"), message);
        Assert.Equal(RunFileState.Pending, review!.Files.Single(f => f.Path == a).State);
    }

    [Fact]
    public async Task ACreatedFile_IsDeletedByItsUndo_AndBroughtBackByARedo()
    {
        var (history, run, _, _, created) = await ARun();

        var (deleted, _) = await Act(history, run, created, RunFileAction.Undo);
        Assert.False(System.IO.File.Exists(created));
        Assert.Equal(Strings.RunFileDeleted("New.cs"), deleted);

        await Act(history, run, created, RunFileAction.Redo);

        Assert.Equal("created by the run", Text(created));
    }

    /// <summary>Two editors on one run: the undo marks only grow, so undo, redo and undo again across them stay
    /// in order — never an older "undone" putting back a file the other editor redid.</summary>
    [Fact]
    public async Task UndoRedoUndo_AcrossTwoEditors_StaysInOrder()
    {
        var (here, run, a, _, _) = await ARun();
        var there = Service();
        var theirs = Assert.Single(there.Runs);

        await FileActionAsync(there, theirs.Id, a, RunFileAction.Undo, _dir, busy: false, CancellationToken.None);
        Assert.Equal("a before", Text(a));
        var (redo, _) = await Act(here, run, a, RunFileAction.Redo);   // this editor sees the other's undo
        Assert.Equal(Strings.RunFileRedone("A.cs"), redo);
        Assert.Equal("a by the run", Text(a));
        var (again, _) = await FileActionAsync(there, Assert.Single(there.Runs).Id, a, RunFileAction.Undo, _dir,
                                               busy: false, CancellationToken.None);

        Assert.Equal(Strings.RunFileRestored("A.cs"), again);
        Assert.Equal("a before", Text(a));
        Assert.True(Assert.Single(here.Runs).IsGivenBack(a));   // the mark that went to disk is the latest one
    }

    [Fact]
    public async Task AKeptFileUndoneByHand_OffersItsRedo()
    {
        var (history, run, a, _, _) = await ARun();
        await Act(history, run, a, RunFileAction.Keep);

        var (_, review) = await Act(history, run, a, RunFileAction.Undo);

        var file = review!.Files.Single(f => f.Path == a);
        Assert.Equal(RunFileState.Undone, file.State);
        Assert.True(file.CanRedo);
    }

    /// <summary>The first runs written carry "undone" and "givenBack": read as undone, never as nothing done.</summary>
    [Fact]
    public async Task ARunInTheFirstFormat_ReadsItsUndoMarks()
    {
        var (_, run, a, _, _) = await ARun();
        var key = RunManifest.KeyIn(_user)!;
        var text = await System.IO.File.ReadAllTextAsync(run.ManifestPath!);
        var json = text[(text.IndexOf('\n') + 1)..];
        var old = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        old.Remove("undoneGeneration");
        old.Remove("generations");
        old["undone"] = true;
        old["givenBack"] = new System.Text.Json.Nodes.JsonArray(a);
        var oldJson = old.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var sign = Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(oldJson)));
        await System.IO.File.WriteAllTextAsync(run.ManifestPath!, "inferpal-run 1 " + sign + "\n" + oldJson);

        var read = Assert.Single(Service().Runs);

        Assert.True(read.Undone);
        Assert.True(read.IsGivenBack(a));
    }

    [Fact]
    public async Task UndoRunReview_ListsEachFileAndItsState_ForAClientWithoutACard()
    {
        var (history, run, a, _, _) = await ARun();
        await System.IO.File.WriteAllTextAsync(a, "a edited by hand after the run");

        var answer = await UndoRunCommandHandler.HandleAsync(history, ["/undo-run", "review"], _dir, CancellationToken.None);

        Assert.StartsWith(Strings.RunReviewHeader(3, run.StartedAt.ToString("HH:mm")), answer, StringComparison.Ordinal);
        Assert.Contains("A.cs", answer, StringComparison.Ordinal);
        Assert.Contains(Strings.RunFileChangedLabel, answer, StringComparison.Ordinal);
        Assert.Contains(Strings.RunFileNew, answer, StringComparison.Ordinal);
        Assert.EndsWith(Strings.RunReviewHowTo(run.Id), answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneFileUndone_ByTyping()
    {
        var (history, run, a, b, _) = await ARun();

        var answer = await UndoRunCommandHandler.HandleAsync(history, ["/undo-run", "#" + run.Id, "A.cs"], _dir,
                                                             CancellationToken.None);

        Assert.Equal(Strings.RunFileRestored("A.cs"), answer);
        Assert.Equal("a before", Text(a));
        Assert.Equal("b by the run", Text(b));
    }

    [Fact]
    public async Task ALongRewrite_ShowsPartOfItsDiff_AndSaysHowMuchIsLeft()
    {
        var big = File("Big.cs", string.Join("\n", Enumerable.Range(0, 300).Select(i => $"old {i}")));
        var history = Service();
        history.BeginRun();
        await Edit(history, big, string.Join("\n", Enumerable.Range(0, 300).Select(i => $"new {i}")));
        history.EndRun();

        var file = Assert.Single(RunReview.Build(history, Assert.Single(history.Runs)).Files);
        var whole = Inferpal.Services.CodeActions.DiffComputer.Compute(
            string.Join("\n", Enumerable.Range(0, 300).Select(i => $"old {i}")),
            string.Join("\n", Enumerable.Range(0, 300).Select(i => $"new {i}"))).Count;

        Assert.True(whole > RunReview.MaxDiffLines, $"{whole} diff lines");   // WITNESS: there is something to cut
        Assert.Equal(RunReview.MaxDiffLines + 1, file.Diff.Count);
        Assert.Equal("…", file.Diff[^1].Prefix);
        Assert.Equal(Strings.RunReviewMoreLines(whole - RunReview.MaxDiffLines), file.Diff[^1].Text);
    }

    [Fact]
    public async Task NothingChangesUnderARunningTurn()
    {
        var (history, run, a, _, _) = await ARun();

        var (message, _) = await FileActionAsync(history, run.Id, a, RunFileAction.Undo, _dir, busy: true, CancellationToken.None);

        Assert.Equal(Strings.UndoRunWhileBusy, message);
        Assert.Equal("a by the run", Text(a));
    }

    /// <summary>A redo writes the run's version over what the undo left: changed since, that change would be lost.</summary>
    [Fact]
    public async Task ARedo_AfterTheFileChangedSinceTheUndo_IsRefused_AndTheChangeStays()
    {
        var (history, run, a, _, _) = await ARun();
        await Act(history, run, a, RunFileAction.Undo);
        await System.IO.File.WriteAllTextAsync(a, "a edited by hand after the undo");

        var (message, review) = await Act(history, run, a, RunFileAction.Redo);

        Assert.Equal("a edited by hand after the undo", Text(a));
        Assert.Equal(Strings.RunFileChangedSinceUndo("A.cs"), message);
        var file = review!.Files.Single(f => f.Path == a);
        Assert.False(file.CanRedo);
        Assert.Equal(Strings.RunFileChangedSinceUndoLabel, RunReview.StateLabel(file));
    }

    /// <summary>A run whose end was never noted (cut short) has no hash to compare: a later run that changed the file
    /// still holds its undo back.</summary>
    [Fact]
    public async Task AFileOfAnUnsealedRun_ThatALaterRunChanged_IsNotUndone()
    {
        var (_, run, a, _, _) = await ARun();
        var key = RunManifest.KeyIn(_user)!;
        var text = await System.IO.File.ReadAllTextAsync(run.ManifestPath!);
        var node = System.Text.Json.Nodes.JsonNode.Parse(text[(text.IndexOf('\n') + 1)..])!.AsObject();
        Assert.True(node.Remove("after"));   // WITNESS: the run did note its end
        var json = node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var sign = Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(json)));
        await System.IO.File.WriteAllTextAsync(run.ManifestPath!, "inferpal-run 1 " + sign + "\n" + json);
        var history = Service();
        history.BeginRun();
        await Edit(history, a, "a by a later run");
        history.EndRun();
        var unsealed = history.Runs.Single(r => r.Id == run.Id);

        var (message, _) = await FileActionAsync(history, unsealed.Id, a, RunFileAction.Undo, _dir, busy: false, CancellationToken.None);

        Assert.Equal(Strings.RunFileChangedSince("A.cs"), message);
        Assert.Equal("a by a later run", Text(a));
    }

    [Fact]
    public async Task AFileKeptThenUndoneByHand_IsNotSaidKept_ByUndoRun()
    {
        var (history, run, _, b, _) = await ARun();
        await Act(history, run, b, RunFileAction.Keep);
        await Act(history, run, b, RunFileAction.Undo);

        var answer = await UndoRunCommandHandler.HandleAsync(history, ["/undo-run"], _dir, CancellationToken.None);

        Assert.Equal("b before", Text(b));
        Assert.DoesNotContain(Strings.UndoRunLeftKept("B.cs"), answer, StringComparison.Ordinal);
    }

    /// <summary>Every file given back one by one is the run undone: an earlier run's undo is no longer held back.</summary>
    [Fact]
    public async Task EveryFileUndoneOneByOne_IsTheRunUndone()
    {
        var (history, run, a, b, created) = await ARun();

        foreach (var file in new[] { a, b, created }) await Act(history, run, file, RunFileAction.Undo);

        Assert.True(Assert.Single(history.Runs).Undone);
    }

    [Fact]
    public async Task AFileWhoseCopyFromBeforeTheRunIsGone_OffersNoUndo_AndShowsNoMadeUpDiff()
    {
        var (history, run, a, _, _) = await ARun();
        System.IO.File.Delete(run.Changes.Single(c => c.OriginalPath == a).SnapshotPath!);

        var file = RunReview.Build(history, run).Files.Single(f => f.Path == a);

        Assert.Equal(RunFileState.NoCopy, file.State);
        Assert.Empty(file.Diff);
        Assert.Equal(Strings.RunFileNoCopyLabel, RunReview.StateLabel(file));
    }

    [Fact]
    public async Task ACreatedFileAlreadyGone_IsSaidAlreadyUndone_NotDeletedAndSaved()
    {
        var (history, run, _, _, created) = await ARun();
        System.IO.File.Delete(created);

        var (message, _) = await Act(history, run, created, RunFileAction.Undo);

        Assert.Equal(Strings.RunFileAlreadyUndone("New.cs"), message);
    }

    [Fact]
    public async Task ARedoWhoseCopyIsGone_IsNotOffered()
    {
        var (history, run, a, _, _) = await ARun();
        await Act(history, run, a, RunFileAction.Undo);
        System.IO.File.Delete(run.RedoSourceOf(a)!);

        var file = RunReview.Build(history, run).Files.Single(f => f.Path == a);

        Assert.Equal(RunFileState.Undone, file.State);   // WITNESS: still undone, only its redo is gone
        Assert.False(file.CanRedo);
    }

    /// <summary>A file the run created is all added lines, however long: the line diff's own cap would say "too large".</summary>
    [Fact]
    public async Task ALongCreatedFile_ShowsItsLines()
    {
        var created = Path.Combine(_dir, "Long.cs");
        var history = Service();
        history.BeginRun();
        await Edit(history, created, string.Join("\n", Enumerable.Range(0, 400).Select(i => $"line {i}")));
        history.EndRun();

        var file = Assert.Single(RunReview.Build(history, Assert.Single(history.Runs)).Files);

        Assert.Equal(RunReview.MaxDiffLines, file.Diff.Count(l => l.Prefix == "+"));
        Assert.Equal("line 0", file.Diff[0].Text);
        Assert.Equal(Strings.RunReviewMoreLines(400 - RunReview.MaxDiffLines), file.Diff[^1].Text);
    }
}
