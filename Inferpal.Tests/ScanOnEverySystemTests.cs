using System.IO;
using System.Text.Json;
using Inferpal.Services.Documents;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A scanned PDF on every system (pari 96): the page pictures taken from the PDF with no renderer, the in-process engine
/// (PP-OCRv6) that reads them on Linux and macOS — and on Windows when Windows has no OCR language — on every leg of the suite,
/// a page in an alphabet it does not read said rather than read as garbage, and the picture fallback for a model that sees
/// (Windows' own OCR: <c>WindowsOnly/PdfOcrTests</c>).
/// </summary>
public sealed class ScanOnEverySystemTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-scan-tests-").FullName;

    public void Dispose()
    {
        PdfOcr.ForTests.Value = null;
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string Scan(string fixture = "invoice-scan.pdf")
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        var path = Path.Combine(_root, fixture);
        File.Copy(Path.Combine(dir!.FullName, "Inferpal.Tests", "Fixtures", "scans", fixture), path);
        return path;
    }

    [Fact]
    public void AScannersPagePicture_IsTakenFromThePdf_AsTheJpegItIs()
    {
        var extracted = PdfPageImages.Extract(File.ReadAllBytes(Scan()), 5, default);

        var page = Assert.Single(extracted.Images);
        Assert.Equal(1, page.Page);
        Assert.Equal(".jpg", page.Extension);
        Assert.True(page.Bytes is [0xFF, 0xD8, ..], "a JPEG starts with its SOI marker");
    }

    [Fact]
    public async Task WhenTheOcrCannotReadIt_AModelThatSees_IsShownThePages_AndWhyIsSaid()
    {
        var path = Scan();
        PdfOcr.ForTests.Value = _ => new OcrReading(OcrStatus.Unavailable, string.Empty, "PaddleOCR", 0, 0,
                                                    "its text is in an alphabet this OCR does not read");
        var inbox = new ToolImageInbox();

        var said = await new ReadFileTool(() => _root, images: inbox).ExecuteAsync(JsonSerializer.SerializeToElement(new { path }), default);

        Assert.Contains("the OCR could not read it", said);
        Assert.Contains("alphabet this OCR does not read", said);
        Assert.Contains("follow this result", said);
        Assert.Single(inbox.Take());
    }

    [Fact]
    public async Task AFailedReading_IsNeverKept_TheNextReadTriesAgain()
    {
        var path = Scan();
        var calls = 0;
        PdfOcr.ForTests.Value = _ => { calls++; return new OcrReading(OcrStatus.Failed, string.Empty, "OCR", 0, 0, "timed out"); };
        await new ReadFileTool(() => _root).ExecuteAsync(JsonSerializer.SerializeToElement(new { path }), default);
        await new ReadFileTool(() => _root).ExecuteAsync(JsonSerializer.SerializeToElement(new { path }), default);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void TheEngineOfEverySystem_ReadsTheScan_TheTableRowsKeptTogether()
    {
        var reading = PdfOcr.Paddle(Scan(), null, default);

        Assert.True(reading.Status == OcrStatus.Read, $"{reading.Status}: {reading.Why} {reading.Skipped}");
        Assert.Equal((1, 1), (reading.PagesRead, reading.Pages));
        Assert.Contains("Item | Quantity | Unit price | Amount", reading.Text);   // a row, never the column of headers
        Assert.Contains("Sencha green tea 100 g | 4 | 12.50 | 50.00", reading.Text);
        Assert.Contains("Payment within thirty days", reading.Text);
    }

    [Fact]
    public void APageInAnAlphabetNoRecognizerReads_IsSaid_NeverReadAsGarbage()
    {
        var reading = PdfOcr.Paddle(Scan("letter-el-scan.pdf"), null, default);   // Greek: none of the product's languages

        Assert.Equal(OcrStatus.Unavailable, reading.Status);
        Assert.Equal(string.Empty, reading.Text);
        Assert.Contains("alphabet this OCR does not read", reading.Why);
    }

    /// <summary>Every language of the product is read: what the tiny model does not (Russian, Japanese, Korean) by the
    /// recognizer of its alphabet, named to the model.</summary>
    [Theory]
    [InlineData("letter-ru-scan.pdf", "Уважаемый клиент", "Cyrillic")]
    [InlineData("letter-ja-scan.pdf", "お客様各位", "Japanese")]
    [InlineData("letter-ko-scan.pdf", "고객님께", "Korean")]
    public void EveryLanguageOfTheProduct_IsRead(string fixture, string words, string alphabet)
    {
        var reading = PdfOcr.Paddle(Scan(fixture), null, default);

        Assert.True(reading.Status == OcrStatus.Read, $"{reading.Status}: {reading.Why} {reading.Skipped}");
        Assert.Contains(words, reading.Text);
        Assert.Equal($"PaddleOCR with its {alphabet} recognizer", reading.Engine);
    }

    /// <summary>A Latin page is never "rescued" by another alphabet's recognizer: the Cyrillic one reads English too, sure of
    /// itself, with every accent lost — what it read must be in its own alphabet.</summary>
    [Fact]
    public async Task ALatinPage_IsNeverRescuedByAnotherAlphabetsRecognizer()
    {
        var jpeg = PdfPageImages.Extract(File.ReadAllBytes(Scan()), 1, default).Images[0].Bytes;
        var image = StbImageSharp.ImageResult.FromMemory(jpeg, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        using var models = await Sdcb.SimdPaddleOCR.PaddleOcrModelSet.LoadAsync(
            Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny.ChineseV6TinyModels.Default, default);

        Assert.Null(new PdfOcr.Scripts(models).Read(image));
    }

    /// <summary>The recognizers embedded in the Core (Apache-2.0) ship with their licence — PaddlePaddle's copyright line
    /// included — in each package: Visual Studio's VSIX, VS Code's (beside the chat page), the ACP archive.</summary>
    [Fact]
    public void TheRecognizersOfOtherAlphabets_ShipWithTheirLicence_InEveryPackage()
    {
        var root = WebviewRebuildTests.RepoRoot();
        var models = Path.Combine(root, "third-party", "paddleocr-models");
        Assert.Contains("Copyright (c) 2016 PaddlePaddle Authors", File.ReadAllText(Path.Combine(models, "LICENSE")), StringComparison.Ordinal);
        Assert.All(new[] { "eslav", "japan", "korean" }, m => Assert.True(File.Exists(Path.Combine(models, m + ".onnx"))));
        Assert.Contains("<Link>paddleocr-models\\LICENSE</Link>", File.ReadAllText(Path.Combine(root, "Inferpal", "Inferpal.csproj")), StringComparison.Ordinal);
        Assert.Contains("'third-party', 'paddleocr-models', f", File.ReadAllText(Path.Combine(root, "vscode", "esbuild.js")), StringComparison.Ordinal);
        Assert.DoesNotContain("media/paddleocr-models", File.ReadAllText(Path.Combine(root, "vscode", ".vscodeignore")), StringComparison.Ordinal);
        Assert.Contains("third-party/paddleocr-models/$f", File.ReadAllText(Path.Combine(root, "acp", "package.ps1")), StringComparison.Ordinal);
    }

    [Fact]
    public void AMixedDocument_SaysThePagesItLeftOut_AndWhy_NeverOneBadPageForTheWhole()
    {
        byte[] Jpeg(string fixture) => PdfPageImages.Extract(File.ReadAllBytes(Scan(fixture)), 1, default).Images[0].Bytes;
        // A JPEG header that declares 30 000 × 30 000 pixels and holds none: never decoded (gigabytes for nothing).
        byte[] bomb = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x75, 0x30, 0x75, 0x30, 0x03,
                       0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01, 0xFF, 0xD9];
        var path = Path.Combine(_root, "mixed.pdf");
        File.WriteAllBytes(path, ImagePdf(Jpeg("invoice-scan.pdf"), Jpeg("letter-el-scan.pdf"), bomb));

        var reading = PdfOcr.Paddle(path, null, default);

        Assert.True(reading.Status == OcrStatus.Read, $"{reading.Status}: {reading.Why} {reading.Skipped}");
        Assert.Contains("--- page 1 ---", reading.Text);
        Assert.Contains("Payment within thirty days", reading.Text);
        Assert.Equal("page 2: " + "its text is in an alphabet this OCR does not read (it reads Latin, Chinese, Cyrillic, Japanese and Korean)"
                     + "; page 3: a page picture too large or unreadable", reading.Skipped);
        Assert.Contains("[Not read, their text is not here — page 2:", AttachedDocuments.ScanText("mixed.pdf", reading));
    }

    /// <summary>A PDF whose pages are each one JPEG, as a scanner writes it.</summary>
    private static byte[] ImagePdf(params byte[][] jpegs)
    {
        var pdf = new MemoryStream();
        void W(string s) => pdf.Write(System.Text.Encoding.Latin1.GetBytes(s));
        W("%PDF-1.4\n1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
        W($"2 0 obj << /Type /Pages /Kids [{string.Join(" ", jpegs.Select((_, i) => $"{3 + 3 * i} 0 R"))}] /Count {jpegs.Length} >> endobj\n");
        for (var i = 0; i < jpegs.Length; i++)
        {
            int page = 3 + 3 * i, image = page + 1, content = page + 2;
            var draw = System.Text.Encoding.Latin1.GetBytes("q 595 0 0 842 0 0 cm /Im0 Do Q");
            W($"{page} 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /XObject << /Im0 {image} 0 R >> >> /Contents {content} 0 R >> endobj\n");
            W($"{image} 0 obj << /Type /XObject /Subtype /Image /Width 2480 /Height 3508 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpegs[i].Length} >>\nstream\n");
            pdf.Write(jpegs[i]);
            W($"\nendstream endobj\n{content} 0 obj << /Length {draw.Length} >>\nstream\n");
            pdf.Write(draw);
            W("\nendstream endobj\n");
        }
        W("trailer << /Root 1 0 R >>\n%%EOF\n");
        return pdf.ToArray();
    }

    [Fact]
    public void ATableTheOcrReadColumnByColumn_IsPutBackInRows_OnASkewedPage()
    {
        // A page turned up to the right (the long line rises 22 px over 1 300); short cells drawn flat, as the detector does.
        PdfOcr.OcrLine Cell(string text, float x, float y) => new(text, x, 120, y, 55, 0);
        var lines = new List<PdfOcr.OcrLine>
        {
            new("Invoice INV-2026-00042 dated November 12, 2026", 187, 1315, 276, 62, -22f / 1315),
            Cell("Amount", 1786, 367), Cell("Quantity", 1089, 381), Cell("Item", 181, 400),
            Cell("50.00", 1786, 427), Cell("Sencha green tea", 184, 457), Cell("4", 1092, 444),
            Cell("79.80", 1790, 487), Cell("Tasting box", 192, 522), Cell("2", 1092, 508),
            new("Payment within thirty days. Late fees: three times the legal interest rate.", 195, 1443, 782, 54, -26f / 1443),
        };

        Assert.Equal("""
            Invoice INV-2026-00042 dated November 12, 2026
            Item | Quantity | Amount
            Sencha green tea | 4 | 50.00
            Tasting box | 2 | 79.80
            Payment within thirty days. Late fees: three times the legal interest rate.
            """.Replace("\r\n", "\n"), PdfOcr.Rows(lines, 2480));
    }
}
