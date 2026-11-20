using System.IO;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Inferpal.Services.Execution;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A binary file among a run's changes says its kind and size, never lines; an image shows itself, before and after
/// when the run replaced it. Read as text, a PNG the agent saved gives line counts that mean nothing.
/// </summary>
[Collection(CultureSerialCollection.Name)]
public sealed class RunBinaryTests : IDisposable
{
    // Two decodable 1 × 1 PNGs of different bytes.
    private static readonly byte[] Png1 = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
    private static readonly byte[] Png2 = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    private readonly string _dir  = Directory.CreateTempSubdirectory("inferpal-run-binary").FullName;
    private readonly string _user = Directory.CreateTempSubdirectory("inferpal-run-binary-key").FullName;

    public RunBinaryTests() => Strings.ApplyLanguage("en");

    public void Dispose()
    {
        Strings.ApplyLanguage(null);
        try { Directory.Delete(_dir, recursive: true); } catch { /* cleanup */ }
        try { Directory.Delete(_user, recursive: true); } catch { /* cleanup */ }
    }

    private FileHistoryService Service() => new() { WorkspaceRoot = () => _dir, UserHistoryRoot = _user };

    /// <summary>A closed run that wrote <paramref name="after"/> to <paramref name="name"/>, which held
    /// <paramref name="before"/> (<c>null</c>: the run created it).</summary>
    private async Task<(FileHistoryService History, HistoryRun Run, string Path)> ARun(string name, byte[]? before, byte[] after)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (before is not null) await File.WriteAllBytesAsync(path, before);
        var history = Service();
        history.BeginRun();
        var (saved, _) = await history.BackUpBeforeChangeAsync(path, CancellationToken.None);
        Assert.True(saved);
        await File.WriteAllBytesAsync(path, after);
        history.EndRun();
        return (history, Assert.Single(history.Runs), path);
    }

    /// <summary>A PNG header that declares <paramref name="side"/> × <paramref name="side"/> pixels.</summary>
    private static byte[] DeclaringPng(int side)
    {
        var bytes = (byte[])Png1.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), side);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), side);
        return bytes;
    }

    [Fact]
    public async Task AnImageTheRunCreated_SaysItsKindPixelsAndSize_NeverLines()
    {
        var (_, run, _) = await ARun("docs/images/cart.png", null, Png1);

        var line = Assert.Single(RunSummary.Files(run));

        Assert.Equal(Strings.RunBinaryImageNew("PNG", "1", "1", ImageCards.SizeText(Png1.Length)), line.Binary);
        Assert.Equal((0, 0), (line.Added, line.Removed));
        Assert.True(line.Created);
    }

    [Fact]
    public async Task AnImageTheRunReplaced_ShowsItselfBeforeAndNow_InPlaceOfADiff()
    {
        var (history, run, _) = await ARun("summary.png", Png1, Png2);

        var file = Assert.Single(RunReview.Build(history, run).Files);

        Assert.Equal(Strings.RunBinaryImageChanged("PNG", ImageCards.SizeText(Png1.Length), ImageCards.SizeText(Png2.Length)), file.Binary!.Label);
        Assert.StartsWith("data:image/png;base64,", file.Binary.Before, StringComparison.Ordinal);
        Assert.StartsWith("data:image/png;base64,", file.Binary.After, StringComparison.Ordinal);
        Assert.Equal(Strings.RunBinaryBefore("1", "1"), file.Binary.BeforeCaption);
        Assert.Equal(Strings.RunBinaryNow("1", "1"), file.Binary.AfterCaption);
        Assert.Empty(file.Diff);
        Assert.Empty(file.Pieces ?? []);
        Assert.Equal((0, 0), (file.Added, file.Removed));
    }

    [Fact]
    public async Task ABinaryFileThatIsNoImage_SaysItsSizes_AndHasNoPicture()
    {
        byte[] Workbook(int size) => [.. "PK\u0003\u0004"u8.ToArray(), .. new byte[size]];
        var (history, run, _) = await ARun("prices.xlsx", Workbook(18 * 1024), Workbook(19 * 1024));

        var file = Assert.Single(RunReview.Build(history, run).Files);

        Assert.Equal(Strings.RunBinaryFileChanged(ImageCards.SizeText(18 * 1024 + 4), ImageCards.SizeText(19 * 1024 + 4)), file.Binary!.Label);
        Assert.False(file.Binary.HasPictures);                             // nothing to unfold
        Assert.Empty(file.Diff);
    }

    /// <summary>⚠ A few kilobytes can declare a picture whose decoding takes gigabytes: said, never drawn.</summary>
    [Fact]
    public async Task AnImageThatDeclaresTooManyPixels_IsSaid_NeverDecoded()
    {
        var (history, run, _) = await ARun("huge.png", null, DeclaringPng(30_000));
        var tried = Diagnostics.Snapshot().Count(e => e.Context == "ChatImages.Thumbnail");

        var file = Assert.Single(RunReview.Build(history, run).Files);

        Assert.Equal(Strings.RunBinaryImageNew("PNG", "30000", "30000", ImageCards.SizeText(Png1.Length)), file.Binary!.Label);
        Assert.Null(file.Binary.After);
        // Never handed to the decoder: a decode that fails leaves its trace (this test's PNG holds 1 pixel of data).
        Assert.Equal(tried, Diagnostics.Snapshot().Count(e => e.Context == "ChatImages.Thumbnail"));
    }

    /// <summary>A text placeholder replaced by an image is said as the image alone, never "PNG image · 1 KB → …".</summary>
    [Fact]
    public async Task ATextSide_DoesNotCountAsAnImage()
    {
        var (history, run, _) = await ARun("logo.png", "TODO: the logo"u8.ToArray(), Png1);

        var file = Assert.Single(RunReview.Build(history, run).Files);

        Assert.Equal(Strings.RunBinaryImageOne("PNG", ImageCards.SizeText(Png1.Length)), file.Binary!.Label);
        Assert.Null(file.Binary.Before);
    }

    /// <summary>An image the run overwrote with text (SVG markup, base64) is no image any more: never said as one.</summary>
    [Fact]
    public async Task AnImageOverwrittenWithText_IsNotSaidAsAnImage()
    {
        var text = "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray();
        var (history, run, _) = await ARun("icon.png", Png1, text);

        var file = Assert.Single(RunReview.Build(history, run).Files);

        Assert.Equal(Strings.RunBinaryFileChanged(ImageCards.SizeText(Png1.Length), ImageCards.SizeText(text.Length)), file.Binary!.Label);
        Assert.False(file.Binary.HasPictures);
    }

    /// <summary>An image whose copy from before the run is gone was changed, not created: never said as new.</summary>
    [Fact]
    public async Task AnImageWithoutItsCopy_IsNotSaidAsNew()
    {
        var (history, run, _) = await ARun("summary.png", Png1, Png2);
        File.Delete(Assert.Single(run.Changes).SnapshotPath!);

        var line = Assert.Single(RunSummary.Files(run));

        Assert.Equal(Strings.RunBinaryImageOne("PNG", ImageCards.SizeText(Png2.Length)), line.Binary);
    }

    /// <summary>Reference arm: a text file keeps its line counts and its diff.</summary>
    [Fact]
    public async Task ATextFile_KeepsItsLineCounts_AndItsDiff()
    {
        var (history, run, _) = await ARun("Order.cs", "a\nb\n"u8.ToArray(), "a\nB\nc\n"u8.ToArray());

        var file = Assert.Single(RunReview.Build(history, run).Files);

        Assert.Null(file.Binary);
        Assert.Null(Assert.Single(RunSummary.Files(run)).Binary);
        Assert.Equal((2, 1), (file.Added, file.Removed));
        Assert.NotEmpty(file.Diff);
    }

    [Fact]
    public async Task TheReviewAsText_SaysTheKind_NotLines()
    {
        var (history, _, _) = await ARun("cart.png", null, Png1);

        var answer = await UndoRunCommandHandler.HandleAsync(history, ["/undo-run", "review"], _dir, CancellationToken.None);

        Assert.Contains("cart.png · " + Strings.RunBinaryImageNew("PNG", "1", "1", ImageCards.SizeText(Png1.Length)), answer, StringComparison.Ordinal);
        Assert.DoesNotContain("+0", answer, StringComparison.Ordinal);
    }

    /// <summary>The size in the person's units: Ko in French.</summary>
    [Fact]
    public void ASize_IsSaidInThePersonsUnits()
    {
        Assert.Equal("48 KB", ImageCards.SizeText(48 * 1024));
        Strings.ApplyLanguage("fr");
        Assert.Equal("48 Ko", ImageCards.SizeText(48 * 1024));
    }
}
