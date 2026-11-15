using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Inferpal.Services.Documents;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What a code review of the document readers found, each held by its case: words kept apart in OpenDocument, repeated
/// cells and rows, a .doc or .xls that is really RTF, HTML or text, a text box read once, an encryption no password opens,
/// a wrong password never kept, Inferpal's own header never a search hit, and the text formats the agent can edit.
/// </summary>
public sealed class DocumentReviewFixesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-docfix-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private const string OdfNs = "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" "
                               + "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" "
                               + "xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\"";

    private static byte[] Zip(params (string Name, string Text)[] parts)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create))
            foreach (var (name, text) in parts)
                using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write(text);
        return ms.ToArray();
    }

    private static byte[] Odf(string mime, string body) =>
        Zip(("mimetype", mime), ("content.xml", $"<office:document-content {OdfNs}><office:body>{body}</office:body></office:document-content>"));

    /// <summary>A space between two styled spans is text: "Hello world", never "Helloworld".</summary>
    [Fact]
    public void ASpaceBetweenTwoSpans_IsKept()
    {
        var odt = Odf("application/vnd.oasis.opendocument.text",
            "<office:text><text:p><text:span>Hello</text:span> <text:span>world</text:span></text:p></office:text>");
        Assert.Equal("Hello world", DocumentText.Extract(odt, ".odt").Text);
    }

    /// <summary>A cell standing for several columns gives its value in each, an empty run keeps the columns after it in
    /// place, identical rows stored once are written as many times, and a sheet's huge empty tail writes nothing.</summary>
    [Fact]
    public void RepeatedCellsAndRows_KeepTheirColumns()
    {
        var ods = Odf("application/vnd.oasis.opendocument.spreadsheet",
            "<office:spreadsheet><table:table table:name=\"Budget\">"
            + "<table:table-row><table:table-cell table:number-columns-repeated=\"3\"><text:p>5</text:p></table:table-cell>"
            + "<table:table-cell><text:p>12</text:p></table:table-cell></table:table-row>"
            + "<table:table-row><table:table-cell><text:p>A</text:p></table:table-cell>"
            + "<table:table-cell table:number-columns-repeated=\"2\"/><table:table-cell><text:p>D</text:p></table:table-cell>"
            + "<table:table-cell table:number-columns-repeated=\"16000\"/></table:table-row>"
            + "<table:table-row table:number-rows-repeated=\"2\"><table:table-cell><text:p>same</text:p></table:table-cell></table:table-row>"
            + "</table:table></office:spreadsheet>");
        Assert.Equal("# Budget\n5\t5\t5\t12\nA\t\t\tD\nsame\nsame", DocumentText.Extract(ods, ".ods").Text);
    }

    /// <summary>A .doc that is RTF (WordPad), an .xls that is a web page or a text table: read as what they are, never
    /// "damaged".</summary>
    [Fact]
    public void ADocThatIsRtf_AndAnXlsThatIsHtmlOrText_AreRead()
    {
        var rtf = Encoding.ASCII.GetBytes(@"{\rtf1\ansi Rapport mensuel\par}");
        Assert.Equal(DocumentProblem.None, DocumentText.Extract(rtf, ".doc").Problem);
        Assert.Contains("Rapport mensuel", DocumentText.Extract(rtf, ".doc").Text);

        var html = Encoding.UTF8.GetBytes("<table><tr><td>Pays</td><td>Taux</td></tr><tr><td>France</td><td>20</td></tr></table>");
        var fromHtml = DocumentText.Extract(html, ".xls");
        Assert.Equal(DocumentProblem.None, fromHtml.Problem);
        Assert.Contains("France", fromHtml.Text);
        Assert.DoesNotContain("<td>", fromHtml.Text);

        var tsv = Encoding.UTF8.GetBytes("Pays\tTaux\nFrance\t20\n");
        Assert.Equal("Pays\tTaux\nFrance\t20", DocumentText.Extract(tsv, ".xls").Text.Trim());
    }

    /// <summary>A text box is written in mc:Choice and again in mc:Fallback: read once.</summary>
    [Fact]
    public void ATextBox_IsReadOnce()
    {
        const string w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        const string mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
        var docx = Zip(("word/document.xml",
            $"<w:document xmlns:w=\"{w}\" xmlns:mc=\"{mc}\"><w:body><w:p><w:r><mc:AlternateContent>"
            + "<mc:Choice><w:txbxContent><w:p><w:r><w:t>Encadré</w:t></w:r></w:p></w:txbxContent></mc:Choice>"
            + "<mc:Fallback><w:txbxContent><w:p><w:r><w:t>Encadré</w:t></w:r></w:p></w:txbxContent></mc:Fallback>"
            + "</mc:AlternateContent></w:r></w:p></w:body></w:document>"));
        var text = DocumentText.Extract(docx, ".docx").Text;
        Assert.Single(text.Split("Encadré")[1..]);
    }

    /// <summary>A compound file whose encryption this reader cannot open (here none it knows) is unreadable, never a
    /// password card that no password passes; and a password that gives an unreadable document is not kept.</summary>
    [Fact]
    public void AnEncryptionNoPasswordOpens_IsUnreadable_AndAWrongPassword_IsNeverKept()
    {
        byte[] ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0];
        Assert.Equal(DocumentProblem.Unreadable, DocumentText.Extract(ole, ".docx").Problem);
        Assert.False(OfficeCrypto.Opens(ole));

        var path = Path.Combine(_dir, "abime.docx");
        File.WriteAllBytes(path, ole);
        Assert.False(AttachedDocuments.TryPassword(path, "n'importe-quoi"));
        Assert.False(AttachedDocuments.OpenedWithPassword(path));
    }

    /// <summary>Inferpal's own header ("[Text extracted from …: formatting, images and comments are not here.]") is no hit
    /// of search_in_files: a document that never says "images" is not reported as saying it.</summary>
    [Fact]
    public async Task TheExtractionHeader_IsNoSearchHit()
    {
        const string w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        File.WriteAllBytes(Path.Combine(_dir, "note.docx"),
            Zip(("word/document.xml", $"<w:document xmlns:w=\"{w}\"><w:body><w:p><w:r><w:t>Le taux est de vingt pour cent.</w:t></w:r></w:p></w:body></w:document>")));
        var tool = new Inferpal.Services.Tools.SearchInFilesTool(() => _dir);
        var header = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { path = _dir, pattern = "images" }), CancellationToken.None);
        Assert.DoesNotContain("note.docx", header, StringComparison.Ordinal);
        var body = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { path = _dir, pattern = "vingt" }), CancellationToken.None);
        Assert.Contains("note.docx", body, StringComparison.Ordinal);
    }

    /// <summary>A notebook, an RTF, an .eml are text files: the agent's read_file gives them as they are (an edit can
    /// quote them), never the converted view an attachment gets.</summary>
    [Fact]
    public async Task ANotebook_IsReadByTheAgent_AsItIs()
    {
        var path = Path.Combine(_dir, "analyse.ipynb");
        File.WriteAllText(path, "{\"cells\": [{\"cell_type\": \"code\", \"source\": [\"x = 1\"]}], \"nbformat\": 4}");
        var read = await new Inferpal.Services.Tools.ReadFileTool(() => _dir)
            .ExecuteAsync(JsonSerializer.SerializeToElement(new { path }), CancellationToken.None);
        Assert.Contains("\"cell_type\": \"code\"", read, StringComparison.Ordinal);
        Assert.True(DocumentText.IsTextFile(path));
        Assert.True(DocumentText.Handles(path));   // still attached as its cells
    }
}
