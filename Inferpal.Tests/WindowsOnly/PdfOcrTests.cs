using System.IO;
using System.Text.Json;
using Inferpal.Services.Documents;
using Inferpal.Services.Presentation;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A scanned PDF read by Windows' own OCR (pari 96): the fixture is a simulated scan of an invoice (rotated, noisy, JPEG, image
/// only), whose table the OCR reads column by column — the product puts its rows back.
/// </summary>
public sealed class PdfOcrTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-ocr-tests-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string Scan()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        var path = Path.Combine(_root, "invoice-scan.pdf");
        File.Copy(Path.Combine(dir!.FullName, "Inferpal.Tests", "Fixtures", "scans", "invoice-scan.pdf"), path);
        return path;
    }

    [Fact]
    public async Task ReadFile_ReadsAScan_ByOcr_TheTableRowsKeptTogether()
    {
        var path = Scan();

        var said = await new ReadFileTool(() => _root).ExecuteAsync(JsonSerializer.SerializeToElement(new { path }), default);

        Assert.Contains("Text read by OCR (Windows OCR", said);
        Assert.Contains("Cast iron teapot | 1 | 64.00 | 64.00", said);   // a row, never the column of items then the prices
        Assert.Contains("Payment within thirty days", said);
    }

    [Fact]
    public void ASearchOrThePromptReadsNoScanByOcr()
    {
        var path = Scan();
        Assert.Null(AttachedDocuments.ForModel(path));   // the default: no OCR (a search reads many documents, the prompt each turn)
        Assert.Null(MentionController.DocumentRefusal(path));   // an attachment is not refused: its text is read by the OCR
    }
}
