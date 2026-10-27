using System.Text;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>A file of a run cut into changes given back one by one, rebuilt byte for byte (<see cref="RunPieces"/>).</summary>
public class RunPiecesTests
{
    // Three changes, each between lines the run left alone: an insertion, a replacement, a deletion.
    private static readonly string[] Before = ["a", "b", "c", "d", "e", "f", "g", "h"];
    private static readonly string[][] RunSides = [["b", "b2", "b3"], ["d2"], []];
    private static readonly string[][] BeforeSides = [["b"], ["d"], ["g"]];

    private static string Text(IEnumerable<string> lines, string eol, bool finalEol = true)
    {
        var text = string.Join(eol, lines);
        return finalEol ? text + eol : text;
    }

    /// <summary>The file with the changes in <paramref name="undone"/> given back, built line by line, apart from the code.</summary>
    private static IEnumerable<string> Expected(IReadOnlySet<int> undone)
    {
        string[] Side(int piece) => undone.Contains(piece) ? BeforeSides[piece] : RunSides[piece];
        return ["a", .. Side(0), "c", .. Side(1), "e", "f", .. Side(2), "h"];
    }

    private static readonly int[][] AllSubsets = [[], [0], [1], [2], [0, 1], [0, 2], [1, 2], [0, 1, 2]];

    [Theory]
    [InlineData("\n", true)]
    [InlineData("\r\n", true)]
    [InlineData("\r\n", false)]
    [InlineData("\r", true)]
    public void EveryChoiceOfUndoneChanges_RebuildsTheFile_ByteForByte(string eol, bool finalEol)
    {
        var before = Encoding.UTF8.GetBytes(Text(Before, eol, finalEol));
        var run    = Encoding.UTF8.GetBytes(Text(Expected(new HashSet<int>()), eol, finalEol));

        var pieces = RunPieces.Of(before, run, out var why);

        Assert.NotNull(pieces);
        Assert.Equal(RunPieces.Whole.None, why);
        Assert.Equal(3, pieces!.Pieces.Count);
        foreach (var subset in AllSubsets)
        {
            var undone = subset.ToHashSet();
            Assert.Equal(Encoding.UTF8.GetBytes(Text(Expected(undone), eol, finalEol)), pieces.Compose(undone));
        }
    }

    [Fact]
    public void UndoingOneChange_LeavesItsNeighboursLinesExactlyAsTheRunLeftThem()
    {
        var before = Encoding.UTF8.GetBytes(Text(Before, "\n"));
        var run    = Encoding.UTF8.GetBytes(Text(Expected(new HashSet<int>()), "\n"));
        var pieces = RunPieces.Of(before, run, out _)!;

        var lines = Encoding.UTF8.GetString(pieces.Compose(new HashSet<int> { 1 })).Split('\n');

        Assert.Equal(["a", "b", "b2", "b3", "c", "d", "e", "f", "h", ""], lines);
        // Each change knows where it is, on both sides: the first is two lines inserted after "b", the line it keeps.
        Assert.Equal([(3, 3), (4, 6), (7, 9)], pieces.Pieces.Select(p => (p.BeforeLine, p.RunLine)));
        Assert.Empty(pieces.Pieces[0].Removed);
    }

    [Fact]
    public void ChangesSeparatedByOneUnchangedLine_AreTwoChanges()
    {
        var before = Encoding.UTF8.GetBytes("x\nkeep\ny\n");
        var run    = Encoding.UTF8.GetBytes("X\nkeep\nY\n");

        var pieces = RunPieces.Of(before, run, out _)!;

        Assert.Equal(2, pieces.Pieces.Count);
        Assert.Equal("x\nkeep\nY\n", Encoding.UTF8.GetString(pieces.Compose(new HashSet<int> { 0 })));
        Assert.Equal("X\nkeep\ny\n", Encoding.UTF8.GetString(pieces.Compose(new HashSet<int> { 1 })));
    }

    [Fact]
    public void AByteOrderMark_AndALegacyCodePage_SurviveTheRebuild()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF];
        var withBom = RunPieces.Of([.. bom, .. "un\ndeux\n"u8.ToArray()], [.. bom, .. "un\nzwei\n"u8.ToArray()], out _)!;
        Assert.Equal([.. bom, .. "un\ndeux\n"u8.ToArray()], withBom.Compose(new HashSet<int> { 0 }));

        // Windows-1252 bytes that are not UTF-8: "café" and "naïve".
        byte[] legacyBefore = [.. "caf"u8.ToArray(), 0xE9, (byte)'\n', .. "na"u8.ToArray(), 0xEF, .. "ve\n"u8.ToArray()];
        byte[] legacyRun    = [.. "caf"u8.ToArray(), 0xE9, (byte)'\n', .. "na"u8.ToArray(), 0xEF, .. "ve!\n"u8.ToArray()];
        var legacy = RunPieces.Of(legacyBefore, legacyRun, out var why);
        Assert.Equal(RunPieces.Whole.None, why);
        Assert.Equal(legacyBefore, legacy!.Compose(new HashSet<int> { 0 }));
        Assert.Equal(legacyRun, legacy.Compose(new HashSet<int>()));
    }

    [Fact]
    public void AFileWhoseEncodingTheRunChanged_IsUndoneWholeOnly()
    {
        // The same text, a byte order mark added by the run: no change can give back the bytes before it.
        var pieces = RunPieces.Of("un\ndeux\n"u8.ToArray(), [0xEF, 0xBB, 0xBF, .. "un\nzwei\n"u8.ToArray()], out var why);

        Assert.Null(pieces);
        Assert.Equal(RunPieces.Whole.NotRebuilt, why);
    }

    [Fact]
    public void TooManyChangedLines_OrABinaryFile_AreUndoneWholeOnly()
    {
        var many = string.Concat(Enumerable.Range(0, RunPieces.MaxDifferingLines + 1).Select(i => $"line {i}\n"));
        Assert.Null(RunPieces.Of([], Encoding.UTF8.GetBytes(many), out var tooLarge));
        Assert.Equal(RunPieces.Whole.TooLarge, tooLarge);

        // Just under the bound, a long change is still one change.
        var fewer = string.Concat(Enumerable.Range(0, RunPieces.MaxDifferingLines).Select(i => $"line {i}\n"));
        Assert.Single(RunPieces.Of("first\n"u8.ToArray(), Encoding.UTF8.GetBytes("first\n" + fewer), out _)!.Pieces);

        Assert.Null(RunPieces.Of([1, 0, 2], [1, 0, 3], out var binary));
        Assert.Equal(RunPieces.Whole.Binary, binary);
    }
}
