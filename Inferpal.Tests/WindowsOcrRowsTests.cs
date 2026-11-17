using System.Globalization;
using Inferpal.Services.Documents;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The Windows OCR's pages go through the product's one table reader (<see cref="PdfOcr.Rows"/>), which takes the page's
/// skew out: an OCR that does not straighten its boxes — Windows Server's — gave a scanned invoice's columns one after the
/// other instead of its rows.
/// </summary>
public sealed class WindowsOcrRowsTests
{
    private static string Box(string text, float x, float width, float middle, float slope = 0f) =>
        string.Create(CultureInfo.InvariantCulture,
            $$"""{"t":"{{text}}","x":{{x}},"w":{{width}},"m":{{middle}},"h":30,"s":{{slope}}}""");

    [Fact]
    public void ASkewedPage_KeepsItsTableRows()
    {
        // A page 2000 px wide, tilted 2 %: the title line shows it; a cell 1 400 px to the right of its row's first one sits
        // 28 px lower, which is more than half a line's height.
        const float tilt = 0.02f;
        float Mid(float baseline, float x) => baseline + tilt * x;
        var lines = new List<string> { Box("Invoice INV-2026-00042 dated November 12, 2026", 100, 1500, Mid(100, 850), tilt) };
        foreach (var (row, item, qty, price) in new[] { (300f, "Sencha green tea", "4", "12.50"), (360f, "Tasting box", "2", "39.90"),
                                                        (420f, "Cast iron teapot", "1", "64.00") })
        {
            lines.Add(Box(item, 100, 400, Mid(row, 300)));
            lines.Add(Box(qty, 1000, 30, Mid(row, 1015)));
            lines.Add(Box(price, 1500, 120, Mid(row, 1560)));
        }
        var page = "{\"width\":2000,\"lines\":[" + string.Join(",", lines) + "]}";

        var text = PdfOcr.WindowsPages([page]);

        Assert.Contains("Cast iron teapot | 1 | 64.00", text);
        Assert.Contains("Sencha green tea | 4 | 12.50", text);
        Assert.Contains("Invoice INV-2026-00042", text);   // witness: the line outside the table is kept as it is
    }

    [Fact]
    public void AHalfWrittenLastPage_LeavesThePagesBeforeIt()
    {
        var first = "{\"width\":1000,\"lines\":[" + Box("first page", 10, 300, 50) + "]}";
        Assert.Equal("first page", PdfOcr.WindowsPages([first, "{\"width\":1000,\"lines\":[{\"t\":\"cut"]).Trim());
    }
}
