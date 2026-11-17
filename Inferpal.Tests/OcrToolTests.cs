using System.IO;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The <c>ocr</c> tool (pari 108): the text of an image of the workspace or of a scanned PDF, by the OCR built into Inferpal,
/// on every system — a screenshot's error line read exactly, a scan's table in rows, the cases it does not read said.
/// </summary>
public sealed class OcrToolTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-ocr-tool-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        var path = Path.Combine(_root, name);
        File.Copy(Path.Combine(dir!.FullName, "Inferpal.Tests", "Fixtures", "scans", name), path);
        return path;
    }

    private Task<string> Ocr(string path) =>
        new OcrTool(() => _root).ExecuteAsync(JsonSerializer.SerializeToElement(new { path }), default);

    [Fact]
    public async Task AScreenshotOfAnError_GivesItsErrorLine()
    {
        var said = await Ocr(Fixture("error-screenshot.png"));

        Assert.True(said.StartsWith("[Text read by OCR (PaddleOCR) from error-screenshot.png, an image", StringComparison.Ordinal), said);
        Assert.True(said.Contains("CS0246", StringComparison.Ordinal), said);
        Assert.Contains("'ShoppingCart' could not be found", said);
    }

    /// <summary>A dark editor's small text scores lowest of the screenshots measured (30.6, against 28.9 for the highest page
    /// in an alphabet the model does not read): kept, never said as a foreign alphabet.</summary>
    [Fact]
    public async Task ADarkEditorsCode_IsRead_NotTakenForAnotherAlphabet()
    {
        var said = await Ocr(Fixture("editor-screenshot.png"));

        Assert.True(said.StartsWith("[Text read by OCR", StringComparison.Ordinal), said);
        Assert.Contains("CS1002", said);
        Assert.Contains("foreach", said);
    }

    [Fact]
    public async Task AScannedPdf_IsReadTheSameWay_TheTableInRows()
    {
        var said = await Ocr(Fixture("invoice-scan.pdf"));

        Assert.Contains("Text read by OCR", said);
        Assert.Contains("Cast iron teapot", said);
    }

    /// <summary>A BMP (a format the chat's size check does not name: servers do not decode it) is read like a PNG.</summary>
    [Fact]
    public async Task ABitmap_IsRead()
    {
        var png = StbImageSharp.ImageResult.FromMemory(File.ReadAllBytes(Fixture("error-screenshot.png")), StbImageSharp.ColorComponents.RedGreenBlue);
        using (var bmp = File.Create(Path.Combine(_root, "error.bmp")))
            new StbImageWriteSharp.ImageWriter().WriteBmp(png.Data, png.Width, png.Height, StbImageWriteSharp.ColorComponents.RedGreenBlue, bmp);

        var said = await Ocr("error.bmp");

        Assert.True(said.Contains("CS0246", StringComparison.Ordinal), said);
    }

    [Theory]
    [InlineData("error Cs0246: The type", "error CS0246: The type")]                 // the OCR's lowercase second letter
    [InlineData("MSb4019 and Netsdk1004", "MSB4019 and NETSDK1004")]
    [InlineData("commit ca12345, id ts2023", "commit ca12345, id ts2023")]           // a hash, an identifier: left as written
    [InlineData("part CA123 of FS30", "part CA123 of FS30")]                         // three digits or fewer: no code
    public void AnErrorCodesCase_IsPutBack_AndNothingElse(string read, string expected) =>
        Assert.Equal(expected, Inferpal.Services.Documents.PdfOcr.ErrorCodes(read));

    [Fact]
    public async Task WhatItDoesNotRead_IsSaid()
    {
        File.WriteAllBytes(Path.Combine(_root, "locked.pdf"), DocumentTextTests.EncryptedPdf("secret text", "pw", aes256: true));
        File.WriteAllBytes(Path.Combine(_root, "photo.webp"), [0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50]);
        Assert.Contains("cannot be read: it opens only with a password", await Ocr("locked.pdf"));
        Assert.Contains("WebP image, a format this OCR does not decode", await Ocr("photo.webp"));
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "text");
        File.WriteAllBytes(Path.Combine(_root, "real.pdf"), DocumentTextTests.Pdf("BT /F1 12 Tf 72 720 Td (a real text layer) Tj ET"));

        Assert.Contains("read it with read_file", await Ocr("notes.txt"));
        Assert.Contains("holds text of its own", await Ocr("real.pdf"));
        Assert.Contains("is a folder", await Ocr("."));
    }
}
