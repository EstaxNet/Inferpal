using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Inferpal.Services.Documents;

/// <summary>
/// The text of a presentation (<c>.pptx</c>): its slides in the order the deck shows them, each under its number, a
/// paragraph per line, then its speaker notes. Read as zip + XML, like a DOCX (OpenXml SDK reads it as well but weighs
/// 6.6 MB in every package — docs/probes/pari89).
/// </summary>
internal static class PresentationText
{
    private const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    /// <summary>One slide part read at most once inflated.</summary>
    private const long MaxPartBytes = 16L * 1024 * 1024;

    internal static DocumentExtraction Extract(byte[] bytes, CancellationToken ct, string? password)
    {
        if (OfficeCrypto.IsCompoundFile(bytes))
        {
            if (password is null || OfficeCrypto.Decrypt(bytes, password, ct) is not { } package)
                return new(string.Empty, 0, 0, DocumentProblem.Encrypted);
            bytes = package;
        }
        SpreadsheetText.RefuseInflatingPast(bytes);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

        // The deck's order: presentation.xml lists the slides by relationship id, its rels name their parts.
        var targets = Relationships(zip, "ppt/_rels/presentation.xml.rels", "ppt/");
        var order = new List<string>();
        using (var reader = Open(zip, "ppt/presentation.xml"))
        {
            if (reader is null) return new(string.Empty, 0, 0, DocumentProblem.Unreadable);
            while (reader.Read())
                if (reader is { NodeType: XmlNodeType.Element, LocalName: "sldId", NamespaceURI: P }
                    && reader.GetAttribute("id", R) is { } id && targets.TryGetValue(id, out var part))
                    order.Add(part);
        }

        var text = new StringBuilder();
        var withoutText = 0;
        var stoppedAt = 0;
        for (var i = 0; i < order.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var slide = Paragraphs(zip, order[i]);
            var rels = Relationships(zip, RelsOf(order[i]), Path.GetDirectoryName(order[i])!.Replace('\\', '/') + "/");
            var notesPart = rels.Values.FirstOrDefault(t => t.Contains("notesSlide", StringComparison.OrdinalIgnoreCase));
            var notes = notesPart is null ? [] : Paragraphs(zip, notesPart).Where(l => !int.TryParse(l, out _)).ToList();
            if (slide.Count == 0 && notes.Count == 0) withoutText++;
            text.Append("## Slide ").Append(i + 1).Append('\n');
            foreach (var line in slide) text.Append(line).Append('\n');
            if (notes.Count > 0)
            {
                text.Append("Notes:\n");
                foreach (var line in notes) text.Append(line).Append('\n');
            }
            if (text.Length > PdfText.MaxChars) { stoppedAt = i + 1; break; }
        }
        if (order.Count == 0 || withoutText == order.Count) return new(string.Empty, order.Count, 0, DocumentProblem.NoText);
        var read = text.ToString().TrimEnd();
        return new(read.Length > PdfText.MaxChars ? read[..PdfText.MaxChars] : read, order.Count, withoutText, DocumentProblem.None, stoppedAt);
    }

    private static string RelsOf(string part) =>
        Path.GetDirectoryName(part)!.Replace('\\', '/') + "/_rels/" + Path.GetFileName(part) + ".rels";

    /// <summary>A part's relationships, id → target part path (resolved against <paramref name="baseDir"/>).</summary>
    private static Dictionary<string, string> Relationships(ZipArchive zip, string relsPath, string baseDir)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = Open(zip, relsPath);
        if (reader is null) return map;
        while (reader.Read())
            if (reader is { NodeType: XmlNodeType.Element, LocalName: "Relationship", NamespaceURI: Rel }
                && reader.GetAttribute("Id") is { } id && reader.GetAttribute("Target") is { } target)
                map[id] = Resolve(baseDir, target);
        return map;
    }

    private static string Resolve(string baseDir, string target)
    {
        if (target.StartsWith('/')) return target.TrimStart('/');
        var parts = new List<string>(baseDir.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var segment in target.Split('/'))
        {
            if (segment == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (segment != ".") parts.Add(segment);
        }
        return string.Join('/', parts);
    }

    /// <summary>A slide's (or notes') paragraphs, in document order: its runs joined, a line break kept.</summary>
    private static List<string> Paragraphs(ZipArchive zip, string part)
    {
        var lines = new List<string>();
        using var reader = Open(zip, part);
        if (reader is null) return lines;
        StringBuilder? paragraph = null;
        reader.Read();
        while (!reader.EOF)
        {
            // ⚠ ReadElementContentAsString leaves the reader on the next node: reading again would skip it.
            if (reader is { NodeType: XmlNodeType.Element, LocalName: "t", NamespaceURI: A } && paragraph is not null)
            {
                paragraph.Append(reader.ReadElementContentAsString());
                continue;
            }
            if (reader.NamespaceURI == A)
            {
                if (reader is { NodeType: XmlNodeType.Element, LocalName: "p", IsEmptyElement: false }) paragraph = new StringBuilder();
                else if (reader is { NodeType: XmlNodeType.Element, LocalName: "br" } && paragraph is not null) paragraph.Append('\n');
                else if (reader is { NodeType: XmlNodeType.EndElement, LocalName: "p" } && paragraph is not null)
                {
                    var line = paragraph.ToString().Trim();
                    if (line.Length > 0) lines.Add(line);
                    paragraph = null;
                }
            }
            reader.Read();
        }
        return lines;
    }

    private static XmlReader? Open(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry is null) return null;
        if (entry.Length > MaxPartBytes) throw new DocumentLimitException("A presentation part past its size limit");
        return XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CloseInput = true });
    }
}
