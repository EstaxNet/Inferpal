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

    /// <summary>The text of the document at <paramref name="path"/>, read again only when the file changed.</summary>
    /// <param name="ct">The person's Stop: it ends the read (and propagates), its deadline aside.</param>
    public static DocumentExtraction Read(string path, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return new(string.Empty, 0, 0, DocumentProblem.Unreadable);
        if (info.Length > DocumentText.MaxBytes) return new(string.Empty, 0, 0, DocumentProblem.TooLarge);
        if (Known.TryGetValue(path, out var known) && known.Written == info.LastWriteTimeUtc && known.Length == info.Length)
            return known.Result;
        var result = DocumentText.Extract(path, ct);
        // A read that ran out of time on a busy machine is tried again next time, never kept as the file's answer.
        if (result.Problem == DocumentProblem.TooSlow) return result;
        if (Known.Count > 16) Known.Clear();   // a handful of documents in use at once; never a growing cache
        Known[path] = (info.LastWriteTimeUtc, info.Length, result);
        return result;
    }

    /// <summary>
    /// What the model reads of the document at <paramref name="path"/>: a line that says the text was extracted and what
    /// it lacks — pages that hold only images are not "empty" to the person who sees them —, then the text; null when it
    /// gives none (<see cref="WhyNoText"/> says why).
    /// </summary>
    public static string? ForModel(string path, CancellationToken ct = default)
    {
        var result = Read(path, ct);
        if (result.Problem != DocumentProblem.None) return null;
        var name = Path.GetFileName(path);
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

    /// <summary>Why the document gives no text, in English, for the model; null when it gives some.</summary>
    public static string? WhyNoText(string path) => Read(path).Problem switch
    {
        DocumentProblem.None => null,
        DocumentProblem.NoText => "it holds no text: a scan, or images",
        DocumentProblem.Encrypted => "it opens only with a password",
        DocumentProblem.TooSlow => "it took too long to read",
        DocumentProblem.TooLarge => string.Create(CultureInfo.InvariantCulture, $"it is larger than {DocumentText.MaxBytes / (1024 * 1024)} MB"),
        _ => "it could not be read as a PDF or a Word document",
    };
}
