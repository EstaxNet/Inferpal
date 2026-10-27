using System.IO;
using System.Text;
using Inferpal.Localization;
using Inferpal.Services.Commands;
using Inferpal.Services.Execution;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The changes of a run's file in text — <c>/undo-run review</c> numbers them, <c>/undo-run #run:n</c> gives one back and
/// <c>/undo-run redo #run:n</c> gives it again: where there is no card (an ACP client), the same gestures as the card's.
/// </summary>
[Collection(CultureSerialCollection.Name)]
public sealed class UndoRunPieceCommandTests : IDisposable
{
    private readonly string _dir  = Directory.CreateTempSubdirectory("inferpal-undo-pieces").FullName;
    private readonly string _user = Directory.CreateTempSubdirectory("inferpal-undo-pieces-key").FullName;

    public void Dispose()
    {
        Strings.ApplyLanguage(null);
        try { Directory.Delete(_dir, recursive: true); } catch { /* cleanup */ }
        try { Directory.Delete(_user, recursive: true); } catch { /* cleanup */ }
    }

    private const string Before = "a\nb\nc\nd\ne\nf\ng\nh\n";
    private const string ByTheRun = "a\nb\nb2\nc\nD\ne\nf\nh\n";

    private async Task<(FileHistoryService History, HistoryRun Run, string Path)> ARun(string name = "Orders.cs",
                                                                                      string before = Before, string run = ByTheRun)
    {
        var path = Path.Combine(_dir, name);
        await File.WriteAllTextAsync(path, before);
        var history = new FileHistoryService { WorkspaceRoot = () => _dir, UserHistoryRoot = _user };
        history.BeginRun();
        Assert.True((await history.BackUpBeforeChangeAsync(path, CancellationToken.None)).Item1);
        await File.WriteAllTextAsync(path, run);
        history.EndRun();
        return (history, Assert.Single(history.Runs), path);
    }

    private Task<string> Run(FileHistoryService history, string command) =>
        UndoRunCommandHandler.HandleAsync(history, command.Split(' '), _dir, CancellationToken.None);

    [Fact]
    public async Task TheReview_NumbersEachChange_AndSaysHowToGiveOneBack()
    {
        Strings.ApplyLanguage("en");
        var (history, run, _) = await ARun();
        await history.PieceActionAsync(run, Path.Combine(_dir, "Orders.cs"), 1, PieceAction.Keep, CancellationToken.None);

        var text = await Run(history, "/undo-run review");

        Assert.Contains($"      1. {Strings.RunPieceLine(3)} +1 −0", text);
        Assert.Contains($"      2. {Strings.RunPieceLine(5)} +1 −1 · {Strings.RunFileKeptLabel}", text);
        Assert.Contains($"      3. {Strings.RunPieceLine(8)} +0 −1", text);
        Assert.EndsWith(Strings.RunReviewHowToPieces(run.Id), text);
    }

    [Fact]
    public async Task OneChangeGivenBack_ThenGivenAgain_ByItsNumber()
    {
        Strings.ApplyLanguage("en");
        var (history, run, path) = await ARun();

        var undone = await Run(history, $"/undo-run #{run.Id}:2 Orders.cs");
        Assert.Equal(Strings.RunPieceUndone(2, "Orders.cs"), undone);
        Assert.Equal("a\nb\nb2\nc\nd\ne\nf\nh\n", await File.ReadAllTextAsync(path));

        var redone = await Run(history, $"/undo-run redo #{run.Id}:2 Orders.cs");
        Assert.Equal(Strings.RunPieceRedone(2, "Orders.cs"), redone);
        Assert.Equal(ByTheRun, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(":x Orders.cs")]
    [InlineData(":0 Orders.cs")]
    [InlineData(":2")]
    public async Task AChangeNumberThatIsNotOne_OrNoFile_IsTheUsage_AndNothingIsWritten(string tail)
    {
        Strings.ApplyLanguage("en");
        var (history, run, path) = await ARun();

        Assert.Equal(Strings.SlashUsage("/undo-run [list|review]"), await Run(history, $"/undo-run #{run.Id}{tail}"));
        Assert.Equal(ByTheRun, await File.ReadAllTextAsync(path));
        Assert.False(run.Undone);
    }

    [Fact]
    public async Task AChangeTheFileDoesNotHave_IsNamed()
    {
        Strings.ApplyLanguage("en");
        var (history, run, _) = await ARun();

        Assert.Equal(Strings.RunPieceNoSuch("Orders.cs", 4), await Run(history, $"/undo-run #{run.Id}:4 Orders.cs"));
    }

    [Fact]
    public async Task UndoRun_SaysWhichFilesKeptChanges()
    {
        Strings.ApplyLanguage("en");
        var (history, run, path) = await ARun();
        await history.PieceActionAsync(run, path, 0, PieceAction.Keep, CancellationToken.None);

        var text = await Run(history, "/undo-run");

        Assert.Contains(Strings.UndoRunLeftPieces("Orders.cs"), text);
        Assert.Equal("a\nb\nb2\nc\nd\ne\nf\ng\nh\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task AFileTooLongToCut_SaysWhyItGoesBackWhole()
    {
        Strings.ApplyLanguage("en");
        var many = string.Concat(Enumerable.Range(0, RunPieces.MaxDifferingLines + 1).Select(i => $"line {i}\n"));
        var (history, run, _) = await ARun("Seed.sql", "first\n", "first\n" + many);

        var review = RunReview.Build(history, run);
        Assert.Equal(RunPieces.Whole.TooLarge, Assert.Single(review.Files).WholeOnly);
        Assert.Contains(Strings.RunPiecesWholeLabel(Strings.RunPiecesWholeTooLarge(RunPieces.MaxDifferingLines)), await Run(history, "/undo-run review"));
        Assert.Equal(Strings.RunPieceWholeOnly("Seed.sql", Strings.RunPiecesWholeTooLarge(RunPieces.MaxDifferingLines)),
                     await Run(history, $"/undo-run #{run.Id}:1 Seed.sql"));
    }

    [Fact]
    public async Task AFileWithOneChange_IsReviewedAsAFile()
    {
        var (history, run, _) = await ARun("One.cs", "a\nb\n", "a\nB\n");

        Assert.Empty(Assert.Single(RunReview.Build(history, run).Files).Pieces!);
    }
}
