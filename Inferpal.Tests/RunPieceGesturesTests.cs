using System.IO;
using System.Text;
using Inferpal.Services.Execution;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// One change of a file of a run given back, given again, or kept: the file rewritten byte for byte, the neighbours
/// untouched, each write saved first, a file changed since never overwritten — and the marks read back after a restart.
/// </summary>
public sealed class RunPieceGesturesTests : IDisposable
{
    private readonly string _dir  = Directory.CreateTempSubdirectory("inferpal-run-pieces").FullName;
    private readonly string _user = Directory.CreateTempSubdirectory("inferpal-run-pieces-key").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* cleanup */ }
        try { Directory.Delete(_user, recursive: true); } catch { /* cleanup */ }
    }

    private FileHistoryService Service() => new() { WorkspaceRoot = () => _dir, UserHistoryRoot = _user };

    // CRLF, no newline at the end: three changes (an insertion, a replacement, a deletion) between untouched lines.
    private const string Before = "a\r\nb\r\nc\r\nd\r\ne\r\nf\r\ng\r\nh";
    private const string ByTheRun = "a\r\nb\r\nb2\r\nc\r\nD\r\ne\r\nf\r\nh";

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] Read(string path) => File.ReadAllBytes(path);

    /// <summary>A closed run that writes each (file, bytes) pair.</summary>
    private static async Task<HistoryRun> RunAsync(FileHistoryService history, params (string Path, byte[] Bytes)[] writes)
    {
        history.BeginRun();
        foreach (var (path, bytes) in writes)
        {
            Assert.True((await history.BackUpBeforeChangeAsync(path, CancellationToken.None)).Item1);
            await File.WriteAllBytesAsync(path, bytes);
        }
        history.EndRun();
        return history.Runs.OrderByDescending(r => r.Id, StringComparer.Ordinal).First();
    }

    private async Task<(FileHistoryService History, HistoryRun Run, string Path)> ARun(byte[]? before = null, byte[]? run = null)
    {
        var path = Path.Combine(_dir, "Orders.cs");
        await File.WriteAllBytesAsync(path, before ?? Bytes(Before));
        var history = Service();
        return (history, await RunAsync(history, (path, run ?? Bytes(ByTheRun))), path);
    }

    private static async Task<PieceOutcome> Act(FileHistoryService history, HistoryRun run, string path, int index, PieceAction action) =>
        (await history.PieceActionAsync(run, path, index, action, CancellationToken.None)).Outcome;

    private int Copies() => Directory.EnumerateFiles(Path.Combine(_dir, ".inferpal", "history")).Count(f => !Path.GetFileName(f).StartsWith('.'));

    /// <summary>
    /// Two changes that belong together (a brace opened in one, closed in the other): giving back one alone leaves a file
    /// that no longer parses, and the sentence says so with the line — never "back as it was" alone. A change that stands
    /// on its own says nothing more.
    /// </summary>
    [Fact]
    public async Task GivingBackOneOfTwoChangesThatGoTogether_SaysTheFileNoLongerParses()
    {
        var before = Bytes("class A\n{\n    int a;\n    int b;\n    int c;\n}\n");
        var byRun  = Bytes("class A\n{\n    void M() {\n    int b;\n    }\n}\n");
        var (history, run, path) = await ARun(before, byRun);

        var said = await PieceAsync(history, run, path, 2, Inferpal.Services.Commands.UndoRunCommandHandler.RunFileAction.Undo);

        Assert.Contains(Localization.Strings.RunPieceUndone(2, "Orders.cs"), said);                         // witness: given back
        Assert.Equal("class A\n{\n    void M() {\n    int b;\n    int c;\n}\n", File.ReadAllText(path));     // the file broken
        Assert.Contains(await BrokenSentenceAsync(path), said);

        var redone = await PieceAsync(history, run, path, 2, Inferpal.Services.Commands.UndoRunCommandHandler.RunFileAction.Redo);
        Assert.DoesNotContain("⚠", redone);                                                                    // reference arm
        Assert.Equal(byRun, Read(path));
    }

    /// <summary>Redo breaks the file the same way when its partner stays given back: said too.</summary>
    [Fact]
    public async Task RedoingOneOfTwoChangesThatGoTogether_SaysTheFileNoLongerParses()
    {
        var before = Bytes("class A\n{\n    int a;\n    int b;\n    int c;\n}\n");
        var byRun  = Bytes("class A\n{\n    void M() {\n    int b;\n    }\n}\n");
        var (history, run, path) = await ARun(before, byRun);
        await PieceAsync(history, run, path, 1, Inferpal.Services.Commands.UndoRunCommandHandler.RunFileAction.Undo);
        await PieceAsync(history, run, path, 2, Inferpal.Services.Commands.UndoRunCommandHandler.RunFileAction.Undo);
        Assert.Equal(before, Read(path));                                                                     // witness: both back

        var said = await PieceAsync(history, run, path, 2, Inferpal.Services.Commands.UndoRunCommandHandler.RunFileAction.Redo);

        Assert.Contains(await BrokenSentenceAsync(path), said);
    }

    /// <summary>A file that did not parse before the gesture is not this gesture's doing: nothing more is said.</summary>
    [Fact]
    public async Task AFileThatDidNotParseBeforeTheGesture_GetsNoSyntaxSentence()
    {
        var before = Bytes("class A\n{\n    int a;\n    int b;\n    int c;\n");      // no closing brace, before the run
        var byRun  = Bytes("class A\n{\n    int x;\n    int b;\n    int z;\n");
        var (history, run, path) = await ARun(before, byRun);

        var said = await PieceAsync(history, run, path, 2, Inferpal.Services.Commands.UndoRunCommandHandler.RunFileAction.Undo);

        Assert.Contains(Localization.Strings.RunPieceUndone(2, "Orders.cs"), said);                         // witness: given back
        Assert.DoesNotContain("⚠", said);
    }

    private async Task<string> PieceAsync(FileHistoryService history, HistoryRun run, string path, int number,
                                          Inferpal.Services.Commands.UndoRunCommandHandler.RunFileAction action) =>
        (await Inferpal.Services.Commands.UndoRunCommandHandler.PieceActionAsync(
            history, run.Id, path, number, action, _dir, busy: false, CancellationToken.None)).Message;

    /// <summary>The sentence for the parse error <paramref name="path"/> holds now.</summary>
    private static async Task<string> BrokenSentenceAsync(string path)
    {
        var broken = await Inferpal.Services.Tools.SyntaxGuard.FirstErrorAsync(path, File.ReadAllText(path), CancellationToken.None);
        Assert.NotNull(broken);
        return Localization.Strings.RunPieceBrokeSyntax(broken.Value.Line, broken.Value.Message);
    }

    [Fact]
    public async Task OneChangeGivenBack_TheOthersStay_AndARedoGivesTheRunsBytesBack()
    {
        var (history, run, path) = await ARun();

        Assert.Equal(PieceOutcome.Undone, await Act(history, run, path, 1, PieceAction.Undo));
        Assert.Equal(Bytes("a\r\nb\r\nb2\r\nc\r\nd\r\ne\r\nf\r\nh"), Read(path));

        Assert.Equal(PieceOutcome.Undone, await Act(history, run, path, 2, PieceAction.Undo));
        Assert.Equal(Bytes("a\r\nb\r\nb2\r\nc\r\nd\r\ne\r\nf\r\ng\r\nh"), Read(path));

        Assert.Equal(PieceOutcome.Redone, await Act(history, run, path, 1, PieceAction.Redo));
        Assert.Equal(PieceOutcome.Redone, await Act(history, run, path, 2, PieceAction.Redo));
        Assert.Equal(Bytes(ByTheRun), Read(path));
        Assert.Equal(PieceOutcome.Unchanged, await Act(history, run, path, 2, PieceAction.Redo));
    }

    [Fact]
    public async Task EveryWrite_IsSavedFirst_NotOnlyTheFirst()
    {
        var (history, run, path) = await ARun();
        var copies = Copies();

        Assert.Equal(PieceOutcome.Undone, await Act(history, run, path, 1, PieceAction.Undo));
        var afterFirst = Copies();
        Assert.Equal(PieceOutcome.Undone, await Act(history, run, path, 2, PieceAction.Undo));
        var afterSecond = Copies();
        Assert.Equal(PieceOutcome.Redone, await Act(history, run, path, 2, PieceAction.Redo));

        Assert.True(afterFirst > copies);
        Assert.True(afterSecond > afterFirst);
        Assert.True(Copies() > afterSecond);
    }

    [Fact]
    public async Task EveryChangeGivenBack_IsTheFileBeforeTheRun_ByteForByte()
    {
        var (history, run, path) = await ARun();

        foreach (var index in new[] { 2, 0, 1 })
            Assert.Equal(PieceOutcome.Undone, await Act(history, run, path, index, PieceAction.Undo));

        Assert.Equal(Bytes(Before), Read(path));
    }

    [Fact]
    public async Task AByteOrderMark_AndCarriageReturnsAlone_SurviveTheGestures()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF];
        var (history, run, path) = await ARun([.. bom, .. Bytes("a\rb\rc\rd\r")], [.. bom, .. Bytes("a\rB\rc\rD\r")]);

        Assert.Equal(PieceOutcome.Undone, await Act(history, run, path, 0, PieceAction.Undo));
        Assert.Equal([.. bom, .. Bytes("a\rb\rc\rD\r")], Read(path));
        Assert.Equal(PieceOutcome.Redone, await Act(history, run, path, 0, PieceAction.Redo));
        Assert.Equal([.. bom, .. Bytes("a\rB\rc\rD\r")], Read(path));
    }

    [Fact]
    public async Task AFileEditedAfterTheRun_IsNeverRewritten()
    {
        var (history, run, path) = await ARun();
        await File.WriteAllBytesAsync(path, Bytes(ByTheRun + "\r\n// mine"));

        Assert.Equal(PieceOutcome.ChangedSince, await Act(history, run, path, 0, PieceAction.Undo));
        Assert.Equal(Bytes(ByTheRun + "\r\n// mine"), Read(path));
    }

    [Fact]
    public async Task AFileDeletedByHandAfterTheRun_IsChangedSince_NotCreatedByTheRun()
    {
        var (history, run, path) = await ARun();
        File.Delete(path);

        Assert.Equal((PieceOutcome.ChangedSince, RunPieces.Whole.None),
                     await history.PieceActionAsync(run, path, 0, PieceAction.Undo, CancellationToken.None));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AFileALaterRunChanged_IsNeverRewrittenByTheEarlierRunsGesture()
    {
        var (history, run, path) = await ARun();
        await RunAsync(history, (path, Bytes(ByTheRun + "\r\nlater")));

        Assert.Equal(PieceOutcome.ChangedSince, await Act(history, run, path, 0, PieceAction.Undo));
        Assert.Equal(Bytes(ByTheRun + "\r\nlater"), Read(path));
    }

    [Fact]
    public async Task AFileEditedAfterAChangeWasGivenBack_IsNeverRewritten_NorUndoneWhole()
    {
        var (history, run, path) = await ARun();
        Assert.Equal(PieceOutcome.Undone, await Act(history, run, path, 0, PieceAction.Undo));
        var mine = Bytes("a\r\nb\r\nc\r\nD\r\ne\r\nf\r\nh\r\n// mine");
        await File.WriteAllBytesAsync(path, mine);

        Assert.Equal(PieceOutcome.ChangedSince, await Act(history, run, path, 0, PieceAction.Redo));
        Assert.Equal(FileUndoOutcome.ChangedSince, await history.UndoFileAsync(run, path, CancellationToken.None));
        Assert.Equal(mine, Read(path));
    }

    [Fact]
    public async Task TwoGesturesAtOnce_BothLand()
    {
        var (history, run, path) = await ARun();

        var outcomes = await Task.WhenAll(
            Task.Run(() => Act(history, run, path, 0, PieceAction.Undo)),
            Task.Run(() => Act(history, run, path, 2, PieceAction.Undo)));

        Assert.All(outcomes, o => Assert.Equal(PieceOutcome.Undone, o));
        Assert.Equal(Bytes("a\r\nb\r\nc\r\nD\r\ne\r\nf\r\ng\r\nh"), Read(path));
    }

    [Fact]
    public async Task KeepingAChangeGivenBack_IsRefused_AndNothingWritten()
    {
        var (history, run, path) = await ARun();
        Assert.Equal(PieceOutcome.Undone, await Act(history, run, path, 1, PieceAction.Undo));
        var undone = Read(path);

        Assert.Equal(PieceOutcome.KeepGivenBack, await Act(history, run, path, 1, PieceAction.Keep));
        Assert.Equal(undone, Read(path));
        Assert.Empty(history.MarksOf(run).Pieces[path].Kept);
    }

    [Fact]
    public async Task UndoRun_LeavesTheKeptChange_CountsTheFile_AndTheFileRedoneWholeIsTheRunsAgain()
    {
        var (history, run, path) = await ARun();
        Assert.Equal(PieceOutcome.Kept, await Act(history, run, path, 1, PieceAction.Keep));
        Assert.Equal(Bytes(ByTheRun), Read(path));   // a mark: nothing written

        var result = await history.UndoRunAsync(run, CancellationToken.None);

        Assert.Equal(Bytes("a\r\nb\r\nc\r\nD\r\ne\r\nf\r\ng\r\nh"), Read(path));
        Assert.Equal([path], result.PiecesKept);
        Assert.Equal([path], result.Restored);
        Assert.Equal(1, result.SavedFirst);
        Assert.Empty(result.Failed);
        Assert.True(run.Undone);

        Assert.Equal(FileUndoOutcome.Redone, await history.RedoFileAsync(run, path, CancellationToken.None));
        Assert.Equal(Bytes(ByTheRun), Read(path));
        Assert.False(run.Undone);
    }

    [Fact]
    public async Task TheKeptChange_StillGoesBackOnItsOwnUndo()
    {
        var (history, run, path) = await ARun();
        Assert.Equal(PieceOutcome.Kept, await Act(history, run, path, 1, PieceAction.Keep));

        Assert.Equal(PieceOutcome.Undone, await Act(history, run, path, 1, PieceAction.Undo));

        Assert.Equal(Bytes("a\r\nb\r\nb2\r\nc\r\nd\r\ne\r\nf\r\nh"), Read(path));
        Assert.Empty(history.MarksOf(run).Pieces[path].Kept);
    }

    [Fact]
    public async Task TheMarks_AreReadBackAfterARestart()
    {
        var (history, run, path) = await ARun();
        Assert.Equal(PieceOutcome.Undone, await Act(history, run, path, 0, PieceAction.Undo));

        var later = Service();
        var reread = Assert.Single(later.Runs);

        Assert.Equal([0], later.MarksOf(reread).Pieces[path].Undone);
        Assert.Equal(PieceOutcome.Redone, await Act(later, reread, path, 0, PieceAction.Redo));
        Assert.Equal(Bytes(ByTheRun), Read(path));
    }

    [Fact]
    public async Task AChangeThatDoesNotExist_IsRefused_AndNothingWritten()
    {
        var (history, run, path) = await ARun();

        Assert.Equal(PieceOutcome.NoSuchPiece, await Act(history, run, path, 3, PieceAction.Undo));
        Assert.Equal(Bytes(ByTheRun), Read(path));
    }

    [Fact]
    public async Task AFileTheRunCreated_GoesWholeOnly_AndSaysSo()
    {
        var created = Path.Combine(_dir, "New.cs");
        var history = Service();
        var run = await RunAsync(history, (created, Bytes("new\n")));

        Assert.Equal((PieceOutcome.WholeOnly, RunPieces.Whole.Created),
                     await history.PieceActionAsync(run, created, 0, PieceAction.Undo, CancellationToken.None));
        Assert.Equal("new\n", await File.ReadAllTextAsync(created));
    }

    [Fact]
    public async Task GoingBackToAQuestion_LeavesAFileWithAKeptChange_AsItIs()
    {
        var path = Path.Combine(_dir, "Orders.cs");
        await File.WriteAllBytesAsync(path, Bytes(Before));
        var history = Service();
        var run = await RunAsync(history, (path, Bytes(ByTheRun)));
        Assert.Equal(PieceOutcome.Kept, await Act(history, run, path, 1, PieceAction.Keep));
        List<SavedMessage> messages = [new("user", "Edit Orders"), new("assistant", "Edited.", Run: run.Id)];

        var outcome = await Rewind.ApplyAsync(history, messages, 1, null);

        Assert.Empty(outcome!.Failed);
        Assert.Equal(RewindFileKind.Kept, Assert.Single(outcome.Plan.Files).Kind);
        Assert.Equal(Bytes(ByTheRun), Read(path));
    }

    [Fact]
    public async Task GoingBackOverTwoRuns_AFileWithAKeptChangeInTheNewer_IsLeft_AndTheOtherFilesGoBack()
    {
        var f = Path.Combine(_dir, "F.cs");
        var g = Path.Combine(_dir, "G.cs");
        await File.WriteAllTextAsync(f, "a\nb\nc\nd\ne\n");
        await File.WriteAllTextAsync(g, "g before\n");
        var history = Service();
        var first = await RunAsync(history, (f, Bytes("A\nb\nc\nd\ne\n")), (g, Bytes("g by run 1\n")));
        var second = await RunAsync(history, (f, Bytes("A\nb\nC\nd\nE\n")));
        Assert.Equal(PieceOutcome.Kept, await Act(history, second, f, 0, PieceAction.Keep));
        List<SavedMessage> messages =
        [
            new("user", "One"), new("assistant", "Done.", Run: first.Id),
            new("user", "Two"), new("assistant", "Done.", Run: second.Id),
        ];

        var outcome = await Rewind.ApplyAsync(history, messages, 1, null);

        Assert.Empty(outcome!.Failed);
        Assert.Equal(RewindRefusal.None, outcome.Plan.Refusal);
        Assert.Equal("A\nb\nC\nd\nE\n", await File.ReadAllTextAsync(f));
        Assert.Equal("g before\n", await File.ReadAllTextAsync(g));
    }
}
