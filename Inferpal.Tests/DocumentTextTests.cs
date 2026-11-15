using System.IO;
using System.IO.Compression;
using System.Text;
using Inferpal.Localization;
using Inferpal.Services.Documents;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A PDF or DOCX the person attaches is read on this machine (pari 85): the text of the common forms of PDF — simple fonts,
/// compressed streams, composite fonts with their ToUnicode map, words spaced by moving the pen —, a DOCX's paragraphs and
/// tables; and what gives no text says why. The corpus measure lives in Probes/DocumentTextProbeTests.
/// </summary>
public sealed class DocumentTextTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-documents-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private const string Helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";

    /// <summary>A one-page PDF: object 4 is the font, object 5 the content; <paramref name="more"/> are objects 6 on.</summary>
    internal static byte[] Pdf(string content, string font = Helvetica, bool flate = false, params string[] more)
    {
        var body = Encoding.Latin1.GetBytes(content);
        var streamDict = $"<< /Length {{0}}{(flate ? " /Filter /FlateDecode" : "")} >>";
        if (flate)
        {
            var packed = new MemoryStream();
            using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(body);
            body = packed.ToArray();
        }
        var pdf = new MemoryStream();
        void W(string s) => pdf.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.4\n");
        W("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
        W("2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n");
        W("3 0 obj << /Type /Page /Parent 2 0 R /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >> endobj\n");
        W($"4 0 obj {font} endobj\n");
        W($"5 0 obj {string.Format(System.Globalization.CultureInfo.InvariantCulture, streamDict, body.Length)}\nstream\n");
        pdf.Write(body);
        W("\nendstream endobj\n");
        for (var i = 0; i < more.Length; i++) W($"{6 + i} 0 obj {more[i]} endobj\n");
        W("trailer << /Root 1 0 R >>\n%%EOF\n");
        return pdf.ToArray();
    }

    private static string Text(byte[] pdf) => DocumentText.Extract(pdf, ".pdf").Text;

    [Fact]
    public void ASimpleFont_GivesItsLines_AndAPenMoveBetweenWords_IsASpace()
    {
        Assert.Equal("Hello invoice\nsecond line",
            Text(Pdf("BT /F1 12 Tf 72 720 Td (Hello invoice) Tj 0 -14 Td (second line) Tj ET")));
        Assert.Equal("Hello world", Text(Pdf("BT /F1 12 Tf 72 720 Td [(Hello)-300(world)] TJ ET")));
        // Reference arm: a kerning that small glues, as the letters do.
        Assert.Equal("Helloworld", Text(Pdf("BT /F1 12 Tf 72 720 Td [(Hello)-20(world)] TJ ET")));
    }

    [Fact]
    public void ACompressedStream_ReadsAsTheSameText()
    {
        Assert.Equal("Compressed text", Text(Pdf("BT /F1 12 Tf 72 720 Td (Compressed text) Tj ET", flate: true)));
    }

    [Fact]
    public void ACompositeFont_IsReadThroughItsToUnicodeMap_TwoBytesACode()
    {
        const string cmap = "/CIDInit /ProcSet findresource begin begincmap 1 begincodespacerange <0000> <FFFF> endcodespacerange "
                          + "2 beginbfchar <0001> <0048> <0002> <00E9> endbfchar endcmap end";
        var pdf = Pdf("BT /F1 12 Tf 72 720 Td <00010002> Tj ET",
            font: "<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /DescendantFonts [6 0 R] /ToUnicode 7 0 R >>",
            more: ["<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /W [1 [500 500]] >>",
                   $"<< /Length {cmap.Length} >>\nstream\n{cmap}\nendstream"]);
        Assert.Equal("Hé", Text(pdf));
    }

    /// <summary>
    /// ⚠ Word writes a ToUnicode map with a two-byte codespace on simple fonts, whose codes are one byte: read by the map's
    /// codespace, every other letter vanished ("Withdrawn" read "Witraw").
    /// </summary>
    [Fact]
    public void ASimpleFont_ReadsOneByteCodes_WhateverItsToUnicodeMapDeclares()
    {
        const string cmap = "begincmap 1 begincodespacerange <0000> <FFFF> endcodespacerange "
                          + "1 beginbfrange <20> <7E> <0020> endbfrange endcmap";
        var pdf = Pdf("BT /F1 12 Tf 72 720 Td (Withdrawn) Tj ET",
            font: "<< /Type /Font /Subtype /TrueType /BaseFont /Arial /Encoding /WinAnsiEncoding /ToUnicode 6 0 R >>",
            more: [$"<< /Length {cmap.Length} >>\nstream\n{cmap}\nendstream"]);
        Assert.Equal("Withdrawn", Text(pdf));
    }

    [Fact]
    public void APageWithoutText_SaysSo_AndANonPdf_IsUnreadable()
    {
        Assert.Equal(DocumentProblem.NoText, DocumentText.Extract(Pdf("q 100 0 0 100 0 0 cm Q"), ".pdf").Problem);
        Assert.Equal(DocumentProblem.Unreadable, DocumentText.Extract("not a pdf at all"u8.ToArray(), ".pdf").Problem);
        Assert.Equal(DocumentProblem.None, DocumentText.Extract(Pdf("BT /F1 12 Tf (x y z) Tj ET"), ".pdf").Problem);   // WITNESS
    }

    /// <summary>
    /// VS Code's three ways to attach a file the person chooses — Browse, the explorer's Attach to Chat, a typed @file —
    /// read it through one helper that sends a PDF, a DOCX or a file VS Code will not open as text to the host's reader:
    /// opened as text, a typed one was left as plain text with nothing said, the other two failed with VS Code's error.
    /// </summary>
    [Fact]
    public void VsCode_AttachesAFileThroughTheHostsReader_WhenItIsNoText()
    {
        // Line endings folded: the public clone keeps this file in CRLF.
        var code = WebviewRebuildTests.TsCode("chatViewProvider.ts").Replace("\r\n", "\n");
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(code, @"this\.readAttachable\(").Count);
        Assert.Contains("host.attachRead(uri.fsPath)", code, StringComparison.Ordinal);
        Assert.Contains(@"/\.(pdf|docx)$/i.test(uri.fsPath)", code, StringComparison.Ordinal);
        // The one remaining openTextDocument of a chosen file is the helper's own.
        var helper = code[code.IndexOf("private async readAttachable(", StringComparison.Ordinal)..];
        helper = helper[..helper.IndexOf("\n  }\n", StringComparison.Ordinal)];
        Assert.Contains("openTextDocument(uri)", helper, StringComparison.Ordinal);
        foreach (var gesture in new[] { "case 'attachBrowse':", "async attachItem(" })
        {
            var body = code[code.IndexOf(gesture, StringComparison.Ordinal)..];
            body = body[..body.IndexOf("\n  }\n", StringComparison.Ordinal)];
            Assert.DoesNotContain("openTextDocument", body, StringComparison.Ordinal);
        }
    }

    // ── The agent's tools: read_file and search_in_files read a document's text, at the same line numbers ────────────

    private Task<string> ReadTool(string path) =>
        new Services.Tools.ReadFileTool(() => _dir).ExecuteAsync(System.Text.Json.JsonSerializer.SerializeToElement(new { path }), CancellationToken.None);

    private Task<string> SearchTool(string pattern) =>
        new Services.Tools.SearchInFilesTool(() => _dir).ExecuteAsync(
            System.Text.Json.JsonSerializer.SerializeToElement(new { path = _dir, pattern }), CancellationToken.None);

    [Fact]
    public async Task ReadFile_ReadsADocumentsText_AndSaysWhyAScanHasNone()
    {
        var spec = Path.Combine(_dir, "spec.pdf");
        File.WriteAllBytes(spec, Pdf("BT /F1 12 Tf 72 720 Td (The rounding rule) Tj 0 -14 Td (applies per line) Tj ET"));
        var read = await ReadTool(spec);
        Assert.Contains("[Text extracted from spec.pdf", read, StringComparison.Ordinal);
        Assert.Contains("applies per line", read, StringComparison.Ordinal);

        var scan = Path.Combine(_dir, "scan.pdf");
        File.WriteAllBytes(scan, Pdf("q 100 0 0 100 0 0 cm Q"));
        Assert.Equal("'scan.pdf' gives no text to read: it holds no text: a scan, or images.", await ReadTool(scan));
    }

    /// <summary>A match in a document is reported at the line read_file shows it on; a document without text is counted,
    /// never passed for one that does not hold the words.</summary>
    [Fact]
    public async Task SearchInFiles_FindsWordsInADocument_AtTheLineReadFileShows()
    {
        var spec = Path.Combine(_dir, "spec.pdf");
        File.WriteAllBytes(spec, Pdf("BT /F1 12 Tf 72 720 Td (first line) Tj 0 -14 Td (the zebra rule) Tj ET"));
        File.WriteAllBytes(Path.Combine(_dir, "scan.pdf"), Pdf("q 100 0 0 100 0 0 cm Q"));
        File.WriteAllBytes(Path.Combine(_dir, "notes.docx"), Docx("<w:p><w:r><w:t>zebra in a docx</w:t></w:r></w:p>"));

        var found = await SearchTool("zebra");
        var pdfHit = found.Split('\n').Single(l => l.StartsWith("spec.pdf:", StringComparison.Ordinal));
        var line = int.Parse(pdfHit.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains("the zebra rule", (await ReadTool(spec)).Split('\n')[line - 1], StringComparison.Ordinal);
        Assert.Contains(found.Split('\n'), l => l.StartsWith("notes.docx:", StringComparison.Ordinal));
        Assert.Contains("1 PDF/DOCX file(s) give no text to search", found, StringComparison.Ordinal);
    }

    // ── A hostile file ends the read fast and says what it is, never crashes the process nor runs on ─────────────────

    private static DocumentExtraction Timed(byte[] bytes, string extension)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = DocumentText.Extract(bytes, extension);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"the read took {watch.Elapsed}");
        return result;
    }

    /// <summary>⚠ Arrays nested a million deep were read by recursion: a stack overflow ends the process, no catch stops it.</summary>
    [Fact]
    public void ArraysNestedVeryDeep_AreRefused_NotReadByRecursion()
    {
        var deep = "%PDF-1.4\n1 0 obj " + new string('[', 1_000_000) + " endobj\ntrailer << /Root 1 0 R >>";
        Assert.Equal(DocumentProblem.Unreadable, Timed(Encoding.Latin1.GetBytes(deep), ".pdf").Problem);
    }

    /// <summary>⚠ A compressed stream that stands for gigabytes was inflated whole: past the stream's limit, reading stops.</summary>
    [Fact]
    public void ACompressedBomb_StopsAtItsLimit_SaidTooLarge()
    {
        var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i < 100; i++) z.Write(zeros);   // 100 MB of zeros, a hundred kilobytes packed
        }
        var pdf = new MemoryStream();
        pdf.Write(Encoding.Latin1.GetBytes($"%PDF-1.4\n1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n2 0 obj << /Type /Pages /Kids [3 0 R] >> endobj\n"
            + $"3 0 obj << /Type /Page /Contents 4 0 R >> endobj\n4 0 obj << /Length {packed.Length} /Filter /FlateDecode >>\nstream\n"));
        pdf.Write(packed.ToArray());
        pdf.Write(Encoding.Latin1.GetBytes("\nendstream endobj\ntrailer << /Root 1 0 R >>"));
        Assert.Equal(DocumentProblem.TooLarge, Timed(pdf.ToArray(), ".pdf").Problem);
    }

    /// <summary>⚠ A form that draws itself ran without end, each level within one short stream's own count.</summary>
    [Fact]
    public void AFormThatDrawsItself_IsDrawnOnce()
    {
        var form = "BT /F1 12 Tf (inside) Tj ET " + string.Concat(Enumerable.Repeat("/X Do ", 200));
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n2 0 obj << /Type /Pages /Kids [3 0 R] >> endobj\n"
            + "3 0 obj << /Type /Page /Resources 6 0 R /Contents 5 0 R >> endobj\n4 0 obj " + Helvetica + " endobj\n"
            + "5 0 obj << /Length 6 >>\nstream\n/X Do \nendstream endobj\n"
            + "6 0 obj << /Font << /F1 4 0 R >> /XObject << /X 7 0 R >> >> endobj\n"
            + $"7 0 obj << /Subtype /Form /Resources 6 0 R /Length {form.Length} >>\nstream\n{form}\nendstream endobj\ntrailer << /Root 1 0 R >>");
        var result = Timed(pdf, ".pdf");
        Assert.Equal(DocumentProblem.None, result.Problem);
        Assert.Equal("inside", result.Text);
    }

    /// <summary>⚠ A widths array naming 65 536-code ranges a hundred thousand times ran for minutes: bounded per font.</summary>
    [Fact]
    public void AFontWithAHugeWidthsArray_StopsAtItsLimit()
    {
        var w = string.Concat(Enumerable.Repeat("0 65535 1 ", 100_000));
        var pdf = Pdf("BT /F1 12 Tf <0001> Tj ET",
            font: "<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /DescendantFonts [6 0 R] >>",
            more: [$"<< /Type /Font /Subtype /CIDFontType2 /W [{w}] >>"]);
        Assert.Contains(Timed(pdf, ".pdf").Problem, new[] { DocumentProblem.TooLarge, DocumentProblem.TooSlow });
    }

    [Fact]
    public void ADocxBomb_StopsAtItsLimit_AndAPasswordDocx_IsSaidLocked_AndStrictReads()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var s = zip.CreateEntry("word/document.xml", CompressionLevel.SmallestSize).Open())
        {
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i < 80; i++) s.Write(zeros);
        }
        Assert.Equal(DocumentProblem.TooLarge, Timed(ms.ToArray(), ".docx").Problem);

        byte[] ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0];
        Assert.Equal(DocumentProblem.Encrypted, Timed(ole, ".docx").Problem);

        var strict = new MemoryStream();
        using (var zip = new ZipArchive(strict, ZipArchiveMode.Create, leaveOpen: true))
        using (var w = new StreamWriter(zip.CreateEntry("word/document.xml").Open()))
            w.Write("<w:document xmlns:w=\"http://purl.oclc.org/ooxml/wordprocessingml/main\"><w:body><w:p><w:r><w:t>strict text</w:t></w:r></w:p></w:body></w:document>");
        Assert.Equal("strict text", Timed(strict.ToArray(), ".docx").Text);
    }

    internal static byte[] Docx(string documentXml)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var w = new StreamWriter(zip.CreateEntry("word/document.xml").Open()))
            w.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" + documentXml + "</w:body></w:document>");
        return ms.ToArray();
    }

    [Fact]
    public void ADocx_GivesItsParagraphsAndTables_NotItsTrackedDeletions()
    {
        var docx = Docx("<w:p><w:r><w:t>Cahier</w:t></w:r><w:r><w:t xml:space=\"preserve\"> des charges</w:t></w:r></w:p>"
                      + "<w:p><w:r><w:t>kept</w:t></w:r><w:del><w:r><w:delText>gone</w:delText></w:r></w:del><w:r><w:tab/><w:t>after</w:t></w:r></w:p>"
                      + "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>Champ</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Type</w:t></w:r></w:p></w:tc></w:tr></w:tbl>");
        Assert.Equal("Cahier des charges\nkept\tafter\nChamp\tType", DocumentText.Extract(docx, ".docx").Text);
    }

    /// <summary>
    /// The one reader of what the person attaches: a PDF with text is attached as that text, under a line that tells the
    /// model it was extracted; one without text is refused with its cause; another binary keeps its refusal.
    /// </summary>
    [Fact]
    public async Task AnAttachedPdf_IsItsText_AndOneWithoutText_SaysWhy()
    {
        var withText = Path.Combine(_dir, "spec.pdf");
        File.WriteAllBytes(withText, Pdf("BT /F1 12 Tf 72 720 Td (The invoice total is rounded) Tj ET"));
        Assert.Null(MentionController.BinaryRefusal(withText));
        var attached = await MentionController.FileMentionTextAsync(withText, null, CancellationToken.None);
        Assert.StartsWith("[Text extracted from spec.pdf (1 page)", attached);
        Assert.Contains("The invoice total is rounded", attached, StringComparison.Ordinal);

        var scan = Path.Combine(_dir, "scan.pdf");
        File.WriteAllBytes(scan, Pdf("q 100 0 0 100 0 0 cm Q"));
        Assert.Equal(Strings.AttachDocumentNoText("scan.pdf"), MentionController.BinaryRefusal(scan));

        var image = Path.Combine(_dir, "logo.png");
        File.WriteAllBytes(image, [0x89, 0x50, 0x4E, 0x47, 0x00, 0x00, 0x01]);
        Assert.Equal(Strings.AttachBinaryFile("logo.png"), MentionController.BinaryRefusal(image));
    }
}
