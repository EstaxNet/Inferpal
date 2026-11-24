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

    /// <summary>
    /// A one-page PDF whose content says <paramref name="text"/>, encrypted with the standard handler: RC4 128 bits
    /// (revision 3) or AES-256 (revision 6), opening only with <paramref name="password"/>.
    /// </summary>
    internal static byte[] EncryptedPdf(string text, string password, bool aes256)
    {
        var content = Encoding.Latin1.GetBytes($"BT /F1 12 Tf 72 720 Td ({text}) Tj ET");
        byte[] body;
        string encrypt;
        var id = new byte[16];
        Random.Shared.NextBytes(id);
        static string Hex(byte[] b) => "<" + Convert.ToHexString(b) + ">";
        if (aes256)
        {
            var fileKey = new byte[32];
            Random.Shared.NextBytes(fileKey);
            var salts = new byte[16];
            Random.Shared.NextBytes(salts);
            var pw = Encoding.UTF8.GetBytes(password);
            var u = PdfDocument.Hash2B(pw, salts[..8], [], 6).Concat(salts).ToArray();
            using var aes = System.Security.Cryptography.Aes.Create();
            aes.Key = PdfDocument.Hash2B(pw, salts[8..], [], 6);
            var ue = aes.EncryptCbc(fileKey, new byte[16], System.Security.Cryptography.PaddingMode.None);
            var iv = new byte[16];
            Random.Shared.NextBytes(iv);
            aes.Key = fileKey;
            body = [.. iv, .. aes.EncryptCbc(content, iv)];
            encrypt = $"<< /Filter /Standard /V 5 /R 6 /Length 256 /CF << /StdCF << /CFM /AESV3 /AuthEvent /DocOpen /Length 32 >> >> " +
                      $"/StmF /StdCF /StrF /StdCF /O {Hex(new byte[48])} /U {Hex(u)} /OE {Hex(new byte[32])} /UE {Hex(ue)} /P -4 /Perms {Hex(new byte[16])} >>";
        }
        else
        {
            static byte[] Padded(string s) => [.. Encoding.Latin1.GetBytes(s).Take(32), .. PdfDocument.Pad.Take(32 - Math.Min(32, s.Length))];
            // Algorithm 3: O from an owner password.
            var ownerKey = System.Security.Cryptography.MD5.HashData(Padded("owner-" + password));
            for (var i = 0; i < 50; i++) ownerKey = System.Security.Cryptography.MD5.HashData(ownerKey);
            var o = PdfDocument.Rc4(ownerKey, Padded(password));
            for (var i = 1; i <= 19; i++) o = PdfDocument.Rc4(ownerKey.Select(k => (byte)(k ^ i)).ToArray(), o);
            // Algorithm 2: the file key; algorithm 5: U.
            var key = System.Security.Cryptography.MD5.HashData([.. Padded(password), .. o, .. BitConverter.GetBytes(-4), .. id]);
            for (var i = 0; i < 50; i++) key = System.Security.Cryptography.MD5.HashData(key);
            var u = PdfDocument.Rc4(key, System.Security.Cryptography.MD5.HashData([.. PdfDocument.Pad, .. id]));
            for (var i = 1; i <= 19; i++) u = PdfDocument.Rc4(key.Select(k => (byte)(k ^ i)).ToArray(), u);
            u = [.. u, .. new byte[16]];
            var objectKey = System.Security.Cryptography.MD5.HashData([.. key, 5, 0, 0, 0, 0]);
            body = PdfDocument.Rc4(objectKey, content);
            encrypt = $"<< /Filter /Standard /V 2 /R 3 /Length 128 /O {Hex(o)} /U {Hex(u)} /P -4 >>";
        }
        var pdf = new MemoryStream();
        void W(string s) => pdf.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.7\n1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n");
        W("3 0 obj << /Type /Page /Parent 2 0 R /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >> endobj\n");
        W($"4 0 obj {Helvetica} endobj\n5 0 obj << /Length {body.Length} >>\nstream\n");
        pdf.Write(body);
        W($"\nendstream endobj\ntrailer << /Size 6 /Root 1 0 R /Encrypt {encrypt} /ID [{Hex(id)} {Hex(id)}] >>\n%%EOF\n");
        return pdf.ToArray();
    }

    /// <summary>A PDF that asks for a password to open: locked without one and with a wrong one, read with the right one —
    /// in both families of the standard handler.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APasswordProtectedPdf_OpensWithItsPassword_Only(bool aes256)
    {
        var pdf = EncryptedPdf("the penalties of article 9", "contrat2026", aes256);
        Assert.Equal(DocumentProblem.Encrypted, DocumentText.Extract(pdf, ".pdf").Problem);
        Assert.Equal(DocumentProblem.Encrypted, DocumentText.Extract(pdf, ".pdf", password: "contrat2025").Problem);
        Assert.Equal("the penalties of article 9", DocumentText.Extract(pdf, ".pdf", password: "contrat2026").Text);
    }

    /// <summary>A DOCX protected to open (agile encryption, as Office writes it since 2010 — the fixture is made by
    /// msoffcrypto-tool, docs/probes/pari85/protected_docx.py): locked without its password or with a wrong one, read with it.</summary>
    [Fact]
    public void APasswordProtectedDocx_OpensWithItsPassword_Only()
    {
        var docx = File.ReadAllBytes(Path.Combine(WebviewRebuildTests.RepoRoot(), "Inferpal.Tests", "Fixtures", "protected.docx"));
        Assert.Equal(DocumentProblem.Encrypted, DocumentText.Extract(docx, ".docx").Problem);
        Assert.Equal(DocumentProblem.Encrypted, DocumentText.Extract(docx, ".docx", password: "s3cr3t-DOCX").Problem);
        var read = DocumentText.Extract(docx, ".docx", password: "s3cr3t-docx");
        Assert.Equal(DocumentProblem.None, read.Problem);
        Assert.Contains("Penalty for late delivery: half a percent a day.", read.Text);
    }

    /// <summary>The password the person typed opens the file and is kept for it, in memory: the next read of that file is
    /// not asked again, another file is.</summary>
    [Fact]
    public void ATypedPassword_IsKeptForThatFile_InMemory()
    {
        var path = Path.Combine(_dir, "contrat.pdf");
        File.WriteAllBytes(path, EncryptedPdf("article nine", "s3cret", aes256: false));
        var other = Path.Combine(_dir, "other.pdf");
        File.WriteAllBytes(other, EncryptedPdf("another file", "s3cret", aes256: false));
        Assert.True(AttachedDocuments.AsksForPassword(path));
        Assert.False(AttachedDocuments.TryPassword(path, "wrong"));
        Assert.True(AttachedDocuments.AsksForPassword(path));
        Assert.True(AttachedDocuments.TryPassword(path, "s3cret"));
        Assert.False(AttachedDocuments.AsksForPassword(path));
        Assert.Contains("article nine", AttachedDocuments.ForModel(path));
        Assert.True(AttachedDocuments.AsksForPassword(other));
    }

    [Fact]
    public void ASimpleFont_GivesItsLines_AndAPenMoveBetweenWords_IsASpace()
    {
        Assert.Equal("Hello invoice\nsecond line",
            Text(Pdf("BT /F1 12 Tf 72 720 Td (Hello invoice) Tj 0 -14 Td (second line) Tj ET")));
        Assert.Equal("Hello world", Text(Pdf("BT /F1 12 Tf 72 720 Td [(Hello)-300(world)] TJ ET")));
        // Reference arm: a kerning that small glues, as the letters do.
        Assert.Equal("Helloworld", Text(Pdf("BT /F1 12 Tf 72 720 Td [(Hello)-20(world)] TJ ET")));
    }

    /// <summary>A tab, a form feed or a NUL separates an object's number from <c>obj</c> as a space does: PDF white space is
    /// one set, the lexer's.</summary>
    [Theory]
    [InlineData("5\t0\tobj")]
    [InlineData("5\f0 obj")]
    public void AnObjectHeaderSpacedOtherwise_IsStillFound(string header)
    {
        var pdf = Encoding.Latin1.GetString(Pdf("BT /F1 12 Tf 72 720 Td (Spaced) Tj ET"));

        Assert.Equal("Spaced", Text(Encoding.Latin1.GetBytes(pdf.Replace("5 0 obj", header, StringComparison.Ordinal))));
    }

    /// <summary>An inline image ends at "EI" after any PDF white space: after a tab, the skip ran to the end of the stream and
    /// took the text after the image with it.</summary>
    [Fact]
    public void TheTextAfterAnInlineImageEndedAfterATab_IsRead() =>
        Assert.Contains("After", Text(Pdf("BT /F1 12 Tf 72 720 Td (Before) Tj ET BI /W 1 /H 1 /CS /G /BPC 8 ID x\tEI\nBT /F1 12 Tf 72 700 Td (After) Tj ET")));

    [Fact]
    public void ACompressedStream_ReadsAsTheSameText()
    {
        Assert.Equal("Compressed text", Text(Pdf("BT /F1 12 Tf 72 720 Td (Compressed text) Tj ET", flate: true)));
    }

    /// <summary>LZW, the compression of older PDFs (scanned archives): a content stream in it read as its text — before, a
    /// 1993 scan with its OCR layer gave "no text".</summary>
    [Fact]
    public void AnLzwStream_IsDecoded()
    {
        var content = Encoding.Latin1.GetBytes("BT /F1 12 Tf 72 720 Td (an old scanned report) Tj ET");
        var pdf = new MemoryStream();
        void W(string s) => pdf.Write(Encoding.Latin1.GetBytes(s));
        var packed = Lzw(content);
        W("%PDF-1.2\n1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n");
        W("3 0 obj << /Type /Page /Parent 2 0 R /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >> endobj\n");
        W($"4 0 obj {Helvetica} endobj\n5 0 obj << /Length {packed.Length} /Filter /LZWDecode >>\nstream\n");
        pdf.Write(packed);
        W("\nendstream endobj\ntrailer << /Root 1 0 R >>\n%%EOF\n");
        Assert.Equal("an old scanned report", Text(pdf.ToArray()));
    }

    /// <summary>A plain LZW encoder (clear code first, 9 to 12 bits, early change), the inverse of the reader's decoder.</summary>
    private static byte[] Lzw(byte[] data)
    {
        var table = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < 256; i++) table[((char)i).ToString()] = i;
        int next = 258, width = 9, buffer = 0, bits = 0;
        var output = new List<byte>();
        void Emit(int code)
        {
            buffer = (buffer << width) | code;
            bits += width;
            while (bits >= 8) { output.Add((byte)(buffer >> (bits - 8))); bits -= 8; buffer &= (1 << bits) - 1; }
        }
        Emit(256);
        var w = string.Empty;
        foreach (var b in data)
        {
            var wc = w + (char)b;
            if (table.ContainsKey(wc)) { w = wc; continue; }
            Emit(table[w]);
            table[wc] = next++;
            if (next + 1 > 1 << width && width < 12) width++;
            w = ((char)b).ToString();
        }
        if (w.Length > 0) Emit(table[w]);
        Emit(257);
        if (bits > 0) output.Add((byte)(buffer << (8 - bits)));
        return [.. output];
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
    /// VS Code's ways to attach a file the person chooses — Browse, the explorer's Attach to Chat, a typed @file, and an SVG
    /// sent as its markup — read it through one helper that sends a PDF, a DOCX or a file VS Code will not open as text to the host's reader:
    /// opened as text, a typed one was left as plain text with nothing said, the other two failed with VS Code's error.
    /// </summary>
    [Fact]
    public void VsCode_AttachesAFileThroughTheHostsReader_WhenItIsNoText()
    {
        // Line endings folded: the public clone keeps this file in CRLF.
        var code = WebviewRebuildTests.TsCode("chatViewProvider.ts").Replace("\r\n", "\n");
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(code, @"this\.readAttachable\(").Count);
        Assert.Contains("host.attachRead(uri.fsPath)", code, StringComparison.Ordinal);
        // The same list as the Core's: a format the Core reads but VS Code opens as text would attach as noise.
        var listed = System.Text.RegularExpressions.Regex.Match(code, @"/\\\.\(([a-z|]+)\)\$/i\.test\(uri\.fsPath\)").Groups[1].Value.Split('|');
        // Plus the zip: a Playwright trace (pari 170) is read by the host, which refuses any other zip as binary.
        Assert.Equal(DocumentText.Extensions.Select(e => e.TrimStart('.')).Append("zip").Order(), listed.Order());
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
        // Every system reads a scan by its OCR (pari 96): here one that finds nothing, the same on every system.
        Inferpal.Services.Documents.PdfOcr.ForTests.Value = _ => new(Inferpal.Services.Documents.OcrStatus.Empty, "", "OCR", 1, 1);
        Assert.Equal("'scan.pdf' is a scan, and the OCR (OCR) found no text in it: a photo, a drawing or a blank page.", await ReadTool(scan));
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

        // A compound file holding no encryption this reader opens is unreadable, never a password card (real protected
        // files: OfficeDocumentsTests, DocumentPasswordTests).
        byte[] ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0];
        Assert.Equal(DocumentProblem.Unreadable, Timed(ole, ".docx").Problem);

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
        // A scan goes to the OCR (every system); one in which it finds nothing is refused as textless — attaching checks
        // through BinaryRefusalAsync, which runs it.
        Inferpal.Services.Documents.PdfOcr.ForTests.Value = _ => new(Inferpal.Services.Documents.OcrStatus.Empty, "", "OCR", 1, 1);
        Assert.Null(MentionController.BinaryRefusal(scan));   // not run yet: the synchronous check leaves it to the OCR
        Assert.Equal(Strings.AttachDocumentNoText("scan.pdf"), await MentionController.BinaryRefusalAsync(scan, CancellationToken.None));
        Assert.Equal(Strings.AttachDocumentNoText("scan.pdf"), MentionController.BinaryRefusal(scan));   // known now

        var image = Path.Combine(_dir, "logo.png");
        File.WriteAllBytes(image, [0x89, 0x50, 0x4E, 0x47, 0x00, 0x00, 0x01]);
        Assert.Equal(Strings.AttachBinaryFile("logo.png"), MentionController.BinaryRefusal(image));
    }
}
