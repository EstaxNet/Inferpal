using System.IO;
using System.IO.Compression;
using Inferpal.Services.Documents;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Workbooks and presentations read as text (pari 89, lot A): <c>.xlsx</c> and <c>.xls</c> by ExcelDataReader, <c>.pptx</c>
/// as zip + XML — each sheet or slide in order, protected ones with their password. The fixtures are made by openpyxl,
/// xlwt, python-pptx and msoffcrypto-tool.
/// </summary>
public sealed class OfficeDocumentsTests
{
    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(WebviewRebuildTests.RepoRoot(), "Inferpal.Tests", "Fixtures", "documents", name));

    [Fact]
    public void AWorkbook_GivesEachSheetUnderItsName_ARowPerLine_DatesAndNumbersTheSameEverywhere()
    {
        var read = DocumentText.Extract(Fixture("factures.xlsx"), ".xlsx");
        Assert.Equal(DocumentProblem.None, read.Problem);
        Assert.Contains("# Factures\n", read.Text);
        Assert.Contains("F-2026-001\tSociété Générale de Fournitures\t1250.5\t0.2", read.Text);
        Assert.Contains("東京商事", read.Text);
        Assert.Contains("2026-03-31", read.Text);
        Assert.Contains("# Notes\n", read.Text);
        Assert.Contains("pénalité de retard", read.Text);
    }

    [Fact]
    public void AnOldBinaryWorkbook_IsReadToo()
    {
        var read = DocumentText.Extract(Fixture("budget.xls"), ".xls");
        Assert.Equal(DocumentProblem.None, read.Problem);
        Assert.Contains("Hébergement\t4200", read.Text);
    }

    [Fact]
    public void APresentation_GivesItsSlidesInOrder_TheirTables_AndTheirNotes()
    {
        var read = DocumentText.Extract(Fixture("revue.pptx"), ".pptx");
        Assert.Equal(DocumentProblem.None, read.Problem);
        Assert.Equal(3, read.Pages);
        var text = read.Text;
        Assert.True(text.IndexOf("## Slide 1\nArchitecture du module de facturation", StringComparison.Ordinal) >= 0, text);
        Assert.True(text.IndexOf("Décisions", StringComparison.Ordinal) < text.IndexOf("## Slide 3", StringComparison.Ordinal), text);
        Assert.Contains("Notes:\nRappeler la contrainte légale", text);
        Assert.Contains("vingt pour cent", text);
    }

    [Theory]
    [InlineData("factures-protege.xlsx", ".xlsx", "Société Générale")]
    [InlineData("revue-protege.pptx", ".pptx", "Arrondi au centime")]
    public void AProtectedWorkbookOrPresentation_OpensWithItsPassword_Only(string name, string extension, string inside)
    {
        var bytes = Fixture(name);
        Assert.Equal(DocumentProblem.Encrypted, DocumentText.Extract(bytes, extension).Problem);
        Assert.Equal(DocumentProblem.Encrypted, DocumentText.Extract(bytes, extension, password: "S3cr3t-a").Problem);
        Assert.Contains(inside, DocumentText.Extract(bytes, extension, password: "s3cr3t-a").Text);
    }

    /// <summary>ExcelDataReader bounds nothing: a sheet that inflates past the limit is refused before it reads it — measured
    /// without the bound, a 4 MB file took it to 3 GB.</summary>
    [Fact]
    public void AWorkbookThatInflatesPastTheLimit_IsRefusedBeforeItIsRead()
    {
        var bomb = new MemoryStream();
        using (var original = new ZipArchive(new MemoryStream(Fixture("factures.xlsx")), ZipArchiveMode.Read))
        using (var zip = new ZipArchive(bomb, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in original.Entries)
            {
                var copy = zip.CreateEntry(entry.FullName, CompressionLevel.Fastest);
                using var to = copy.Open();
                if (entry.FullName == "xl/worksheets/sheet2.xml")
                {
                    var zeros = new byte[1024 * 1024];
                    for (var i = 0; i < (int)(SpreadsheetText.MaxInflatedBytes / zeros.Length) + 1; i++) to.Write(zeros);
                }
                else
                {
                    using var from = entry.Open();
                    from.CopyTo(to);
                }
            }
        }
        Assert.Equal(DocumentProblem.TooLarge, DocumentText.Extract(bomb.ToArray(), ".xlsx").Problem);
    }
}
