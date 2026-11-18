using System.Collections.Concurrent;
using System.Globalization;

namespace Inferpal.Services.Documents;

/// <summary>
/// The one reader of a PDF or DOCX the person attaches, pins or mentions: its text read once per version of the file (a
/// pinned document is read for every request, and a refusal check and the attachment read it twice in one gesture), what
/// the model reads (the text under a line that says it was extracted, and what is missing), and why there is none.
/// </summary>
internal static class AttachedDocuments
{
    private static readonly ConcurrentDictionary<string, (DateTime Written, long Length, DocumentExtraction Result)> Known =
        new(PathComparer.Default);

    /// <summary>
    /// The passwords the person typed, by file, for this process only. ⚠ Never written anywhere — not the settings, the
    /// conversation, its export, a log or the diagnostics — and never sent: only the text they open leaves this class.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> Passwords = new(PathComparer.Default);

    /// <summary>The text of the document at <paramref name="path"/>, read again only when the file changed.</summary>
    /// <param name="ct">The person's Stop: it ends the read (and propagates), its deadline aside.</param>
    public static DocumentExtraction Read(string path, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return new(string.Empty, 0, 0, DocumentProblem.Unreadable);
        if (info.Length > DocumentText.MaxBytesOf(path)) return new(string.Empty, 0, 0, DocumentProblem.TooLarge);
        if (Known.TryGetValue(path, out var known) && known.Written == info.LastWriteTimeUtc && known.Length == info.Length)
            return known.Result;
        var result = DocumentText.Extract(path, ct, Passwords.GetValueOrDefault(path));
        // A read that ran out of time on a busy machine is tried again next time, never kept as the file's answer.
        if (result.Problem == DocumentProblem.TooSlow) return result;
        if (Known.Count > 16) Known.Clear();   // a handful of documents in use at once; never a growing cache
        Known[path] = (info.LastWriteTimeUtc, info.Length, result);
        return result;
    }

    /// <summary>A PDF or DOCX that opens only with a password the person has not given yet in this process.</summary>
    public static bool AsksForPassword(string path) =>
        DocumentText.Handles(path) && Read(path).Problem == DocumentProblem.Encrypted;

    /// <summary>
    /// Opens the PDF or DOCX at <paramref name="path"/> with the <paramref name="password"/> the person typed: true when it does,
    /// and the password is kept in memory for that file until the process ends, so the file is not asked for again.
    /// </summary>
    public static bool TryPassword(string path, string password, CancellationToken ct = default)
    {
        if (password.Length == 0) return false;
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > DocumentText.MaxBytesOf(path)) return false;
        var result = DocumentText.Extract(path, ct, password);
        // ⚠ A wrong password can also come back as damaged: a part with no checksum decrypts to bytes that do not inflate,
        // a size read from garbage runs past the data. Only a document that opened is a right password — kept, it would
        // close the card and read the file as damaged.
        if (result.Problem is DocumentProblem.Encrypted or DocumentProblem.Unreadable) return false;
        Passwords[path] = password;
        // A right password whose read ran out of time: the "locked" answer kept from before must go, or the file would read
        // as locked again until it changed on disk.
        if (result.Problem != DocumentProblem.TooSlow) Known[path] = (info.LastWriteTimeUtc, info.Length, result);
        else Known.TryRemove(path, out _);
        return true;
    }

    /// <summary>The password the person typed for <paramref name="path"/> in this process, for a reader of the same file.</summary>
    internal static string? PasswordFor(string path) => Passwords.GetValueOrDefault(path);

    /// <summary>The password typed for <paramref name="path"/> is known in this process.</summary>
    public static bool OpenedWithPassword(string path) => Passwords.ContainsKey(path);

    /// <summary>
    /// What the model reads of the document at <paramref name="path"/>: a line that says the text was extracted and what
    /// it lacks — pages that hold only images are not "empty" to the person who sees them —, then the text; null when it
    /// gives none (<see cref="WhyNoText"/> says why).
    /// </summary>
    /// <param name="ocr">A PDF that holds no text (a scan) is read by this machine's OCR (<see cref="PdfOcr"/>) — asked by the
    /// reads of ONE document (read_file, an attachment), never by a search or the system prompt, which read many and often.
    /// A scan no OCR read gives <c>null</c>: <see cref="Scan"/> says why.</param>
    public static string? ForModel(string path, CancellationToken ct = default, bool ocr = false)
    {
        var result = Read(path, ct);
        var name = Path.GetFileName(path);
        if (ocr && IsScan(path, result)) return Scan(path, ct) is { Status: OcrStatus.Read } reading ? ScanText(name, reading) : null;
        if (result.Problem != DocumentProblem.None) return null;
        if (result.Header is { } own) return own + "\n\n" + result.Text;
        var pages = result.Pages == 1 ? "1 page" : string.Create(CultureInfo.InvariantCulture, $"{result.Pages} pages");
        var header = result.Pages == 0
            ? $"[Text extracted from {name}: formatting, images and comments are not here.]"
            : result.PagesWithoutText > 0
                ? string.Create(CultureInfo.InvariantCulture,
                    $"[Text extracted from {name} ({pages}): {result.PagesWithoutText} of them hold no text (images or scans) and are not here.]")
                : string.Create(CultureInfo.InvariantCulture, $"[Text extracted from {name} ({pages}): images are not here.]");
        // ⚠ Reading stopped at a limit: what follows is the beginning only, and the model is told — never a whole.
        if (result.StoppedAt > 0)
            header += result.Pages > 0
                ? string.Create(CultureInfo.InvariantCulture, $"\n[Reading stopped after page {result.StoppedAt}: the rest of the document is not here.]")
                : string.Create(CultureInfo.InvariantCulture, $"\n[Reading stopped after {PdfText.MaxChars:N0} characters: the rest of the document is not here.]");
        return header + "\n\n" + result.Text;
    }

    /// <summary>A PDF that holds no text: a scan, for an OCR to read (on every system: <see cref="PdfOcr"/>).</summary>
    public static bool IsScan(string path) => IsScan(path, Read(path));

    private static bool IsScan(string path, DocumentExtraction result) =>
        result.Problem == DocumentProblem.NoText && path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>What the OCR read of a scan, already: <c>null</c> when it has not run on the file as it is now (or its last run
    /// failed, or found no OCR: those are tried again).</summary>
    public static OcrReading? ScanKnown(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && OcrKnown.TryGetValue(path, out var known) && known.Written == info.LastWriteTimeUtc && known.Length == info.Length
            ? known.Reading : null;
    }

    // What the OCR read of a file — text, or none at all — kept until the file changes: a few seconds a page, never paid
    // twice. A failure or a missing OCR is never kept: the next read tries again (the person may install one).
    private static readonly ConcurrentDictionary<string, (DateTime Written, long Length, OcrReading Reading)> OcrKnown = new(PathComparer.Default);
    private static readonly ConcurrentDictionary<string, (DateTime Written, long Length, string Why)> OcrNotRead = new(PathComparer.Default);

    /// <summary>The scan at <paramref name="path"/> read by this machine's OCR, or what was read of it already. ⚠ Seconds a
    /// page: from a worker, never an editor's thread.</summary>
    public static OcrReading Scan(string path, CancellationToken ct)
    {
        if (ScanKnown(path) is { } known) return known;
        // The file's stamp before the OCR: a file saved while it is read keeps the old stamp, so its new text is read again.
        var info = new FileInfo(path);
        var (written, length) = (info.LastWriteTimeUtc, info.Length);
        var reading = PdfOcr.Read(path, Passwords.GetValueOrDefault(path), ct);
        if (OcrKnown.Count > 16 || OcrNotRead.Count > 16) { OcrKnown.Clear(); OcrNotRead.Clear(); }
        // Why it was not read, for the model's line about it (WhyNoText), under the same stamp.
        if (reading.Why is { } why) OcrNotRead[path] = (written, length, why);
        else OcrNotRead.TryRemove(path, out _);
        if (reading.Status is OcrStatus.Read or OcrStatus.Empty || reading.Lasting) OcrKnown[path] = (written, length, reading);
        return reading;
    }

    /// <summary>The scan's text as the model reads it: which OCR read it, that it can be wrong, and how far it went.</summary>
    public static string ScanText(string name, OcrReading reading)
    {
        var header = string.Create(CultureInfo.InvariantCulture,
            $"[Text read by OCR ({reading.Engine}) from {name}, a scan: words and figures can be misread — check what matters against the document; tables are rebuilt row by row, cells between \"|\".]");
        if (reading.PagesRead < reading.Pages)
            header += string.Create(CultureInfo.InvariantCulture, $"\n[Pages 1 to {reading.PagesRead} of {reading.Pages} read: the rest is not here.]");
        if (reading.Skipped is { } skipped)
            header += $"\n[Not read, their text is not here — {skipped}.]";
        return header + "\n\n" + reading.Text;
    }

    /// <summary>Why an event log or a diagram gives no text — never "a scan": it holds no event, or no shape with text and
    /// no link. <c>null</c> for any other document.</summary>
    internal static string? NoTextKind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".evtx" => "it holds no event",
        ".drawio" or ".vsdx" => "it holds no shape with text and no link",
        _ => null,
    };

    /// <summary>Why the document gives no text, in English, for the model; null when it gives some.</summary>
    public static string? WhyNoText(string path) => Read(path).Problem switch
    {
        DocumentProblem.None => null,
        DocumentProblem.NoText when NoTextKind(path) is { } kind => kind,
        DocumentProblem.NoText => ScanKnown(path) is { Status: OcrStatus.Empty } ? "it holds no text: a scan in which the OCR found none"
                                  : OcrNotRead.TryGetValue(path, out var why) && File.GetLastWriteTimeUtc(path) == why.Written
                                      ? $"it holds no text: a scan the OCR could not read ({why.Why})"
                                  : "it holds no text: a scan, or images",
        DocumentProblem.Encrypted => "it opens only with a password",
        DocumentProblem.TooSlow => "it took too long to read",
        DocumentProblem.TooLarge => string.Create(CultureInfo.InvariantCulture, $"it is larger than {DocumentText.MaxBytesOf(path) / (1024 * 1024)} MB"),
        _ => "it could not be read: damaged, or in a form Inferpal does not read",
    };
}
