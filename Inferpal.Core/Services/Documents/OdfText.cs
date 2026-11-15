using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Inferpal.Services.Documents;

/// <summary>
/// The text of an OpenDocument file — <c>.odt</c>, <c>.ods</c>, <c>.odp</c> (LibreOffice, OpenOffice): its
/// <c>content.xml</c> read in order — a paragraph or heading per line, a table row per line with its cells separated by
/// tabs, each sheet under its name, each slide under its number with its notes.
/// </summary>
/// <remarks>No maintained .NET library reads OpenDocument; the format is zip + one XML part, like a DOCX.</remarks>
internal static class OdfText
{
    private const string Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private const string Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private const string Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private const string Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private const string Presentation = "urn:oasis:names:tc:opendocument:xmlns:presentation:1.0";
    private const string Manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
    /// <summary>The content part read at most once inflated.</summary>
    private const long MaxContentBytes = 64L * 1024 * 1024;

    /// <param name="password">The password the person typed for a protected document.</param>
    internal static DocumentExtraction Extract(byte[] bytes, CancellationToken ct, string? password = null)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        // A document protected by a password keeps its parts encrypted, said in its manifest.
        if (OdfCrypto.Protection(zip) is { } protection)
        {
            if (password is null) return new(string.Empty, 0, 0, DocumentProblem.Encrypted);
            switch (OdfCrypto.Open(zip, protection, password, ct))
            {
                case OdfCrypto.Opened.Package package:   // the whole document, a zip of its own (LibreOffice 24.2+)
                    return Extract(package.Bytes, ct);
                case OdfCrypto.Opened.Content part:   // content.xml alone (earlier versions)
                    return Read(new MemoryStream(part.Bytes), ct);
                default:
                    return new(string.Empty, 0, 0, DocumentProblem.Encrypted);
            }
        }
        var entry = zip.GetEntry("content.xml");
        if (entry is null) return new(string.Empty, 0, 0, DocumentProblem.Unreadable);
        if (entry.Length > MaxContentBytes) throw new DocumentLimitException("An OpenDocument content part past its size limit");
        using var content = entry.Open();
        return Read(content, ct);
    }

    /// <summary>The text of a <c>content.xml</c>.</summary>
    private static DocumentExtraction Read(Stream content, CancellationToken ct)
    {
        using var reader = XmlReader.Create(content, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });

        var text = new StringBuilder();
        var line = new StringBuilder();
        var slides = 0;
        var stoppedAt = 0;
        var cellInRow = 0;
        var notesDepth = -1;
        var nodes = 0;
        var spreadsheet = false;   // a table of a spreadsheet is a sheet, named; one inside a text is not
        void EndLine()
        {
            var l = line.ToString().TrimEnd();
            if (l.Length > 0) text.Append(l).Append('\n');
            line.Clear();
        }
        reader.Read();
        while (!reader.EOF)
        {
            if ((++nodes & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
            if (text.Length > PdfText.MaxChars) { stoppedAt = Math.Max(1, slides); break; }
            if (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.SignificantWhitespace)
            {
                line.Append(reader.Value);
                reader.Read();
                continue;
            }
            if (reader.NodeType == XmlNodeType.Element)
            {
                var ns = reader.NamespaceURI;
                var name = reader.LocalName;
                if (ns == Office && name == "spreadsheet") spreadsheet = true;
                else if (ns == Text && name == "s") line.Append(' ', int.TryParse(reader.GetAttribute("c", Text), out var count) ? Math.Clamp(count, 1, 100) : 1);
                else if (ns == Text && name == "tab") line.Append('\t');
                else if (ns == Text && name == "line-break") line.Append('\n');
                else if (ns == Table && name == "table" && spreadsheet && reader.GetAttribute("name", Table) is { } sheet)
                {
                    EndLine();
                    text.Append("# ").Append(sheet).Append('\n');
                }
                else if (ns == Table && name == "table-row") cellInRow = 0;
                else if (ns == Table && name is "table-cell" or "covered-table-cell")
                {
                    // A run of repeated empty cells is one gap, however many it counts (a sheet's tail repeats 16 000).
                    if (cellInRow++ > 0)
                    {
                        while (line.Length > 0 && line[^1] == ' ') line.Length--;
                        line.Append('\t');
                    }
                }
                else if (ns == Draw && name == "page")
                {
                    EndLine();
                    text.Append("## Slide ").Append(++slides).Append('\n');
                }
                else if (ns == Presentation && name == "notes" && !reader.IsEmptyElement)
                {
                    EndLine();
                    text.Append("Notes:\n");
                    notesDepth = reader.Depth;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                var ns = reader.NamespaceURI;
                var name = reader.LocalName;
                if (ns == Text && name is "p" or "h") { if (cellInRow == 0) EndLine(); else line.Append(' '); }
                else if (ns == Table && name == "table-row") { EndLine(); cellInRow = 0; }
                else if (ns == Presentation && name == "notes" && reader.Depth == notesDepth) { EndLine(); notesDepth = -1; }
            }
            reader.Read();
        }
        EndLine();
        var read = string.Join('\n', text.ToString().Split('\n').Select(l => l.TrimEnd('\t', ' '))).Trim();
        if (read.Split('\n').All(l => l.Length == 0 || l.StartsWith("# ", StringComparison.Ordinal) || l.StartsWith("## Slide", StringComparison.Ordinal) || l == "Notes:"))
            return new(string.Empty, slides, 0, DocumentProblem.NoText);
        return new(read.Length > PdfText.MaxChars ? read[..PdfText.MaxChars] : read, slides, 0, DocumentProblem.None, stoppedAt);
    }
}
