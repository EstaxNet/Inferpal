using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Inferpal.Services.Documents;

/// <summary>
/// The text of a DOCX: <c>word/document.xml</c> read in order — a paragraph per line, a table row per line with its cells
/// separated by tabs, tabs and line breaks kept. Formatting, comments and tracked deletions are left out.
/// </summary>
internal static class DocxText
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    /// <summary>The same vocabulary under ISO 29500 Strict, which Word writes when asked to.</summary>
    private const string WStrict = "http://purl.oclc.org/ooxml/wordprocessingml/main";
    private const string MarkupCompatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    /// <summary>The document part read at most once inflated: a few kilobytes of zip can stand for gigabytes of XML.</summary>
    private const long MaxDocumentXmlBytes = 64L * 1024 * 1024;

    private static bool IsW(XmlReader reader) => reader.NamespaceURI is W or WStrict;

    /// <param name="password">The password the person typed for a DOCX protected to open; null when none was.</param>
    internal static DocumentExtraction Extract(byte[] bytes, CancellationToken ct, string? password = null)
    {
        // A DOCX protected by a password is no zip but an encrypted OLE container: locked, unless the password opens it.
        if (OfficeCrypto.IsCompoundFile(bytes))
        {
            if (OfficeCrypto.Unlock(bytes, password, ct, out var shut) is not { } package) return new(string.Empty, 0, 0, shut);
            bytes = package;
        }
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml");
        if (entry is null) return new(string.Empty, 0, 0, DocumentProblem.Unreadable);
        if (entry.Length > MaxDocumentXmlBytes) throw new DocumentLimitException("A DOCX document part past its size limit");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });

        var text = new StringBuilder();
        var deleted = 0;   // inside a tracked deletion: not the document's text
        var cellInRow = 0;
        var nodes = 0;
        var stoppedAt = 0;
        reader.Read();
        while (!reader.EOF)
        {
            if ((++nodes & 4095) == 0) ct.ThrowIfCancellationRequested();
            if (text.Length > PdfText.MaxChars) { text.Length = PdfText.MaxChars; stoppedAt = 1; break; }
            // ⚠ ReadElementContentAsString already moves past the element: the loop must not Read again after it.
            if (reader.NodeType == XmlNodeType.Element && IsW(reader) && reader.LocalName == "t")
            {
                var run = reader.ReadElementContentAsString();
                if (deleted == 0) text.Append(run);
                continue;
            }
            // A text box is written twice, in mc:Choice and again in mc:Fallback for older readers: one of them, never both.
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Fallback" && reader.NamespaceURI == MarkupCompatibility)
            {
                reader.Skip();
                continue;
            }
            if (!IsW(reader)) { reader.Read(); continue; }
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "del" when !reader.IsEmptyElement: deleted++; break;
                    case "tab": text.Append('\t'); break;
                    case "br" or "cr": text.Append('\n'); break;
                    case "tr": cellInRow = 0; break;
                    case "tc":
                        if (cellInRow++ > 0) { TrimEnd(text); text.Append('\t'); }
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                switch (reader.LocalName)
                {
                    case "del": deleted = Math.Max(0, deleted - 1); break;
                    // A paragraph ends a line, except inside a table cell, whose row ends it.
                    case "p" when cellInRow == 0: text.Append('\n'); break;
                    case "p": text.Append(' '); break;
                    case "tr": TrimEnd(text); text.Append('\n'); cellInRow = 0; break;
                }
            }
            reader.Read();
        }
        var result = text.ToString().Trim();
        return new(result, 0, 0, result.Length == 0 ? DocumentProblem.NoText : DocumentProblem.None, stoppedAt);
    }

    private static void TrimEnd(StringBuilder text)
    {
        while (text.Length > 0 && text[^1] == ' ') text.Length--;
    }
}
