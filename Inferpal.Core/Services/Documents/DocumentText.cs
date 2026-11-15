namespace Inferpal.Services.Documents;

/// <summary>Why a document gave no text, or only part of it.</summary>
internal enum DocumentProblem
{
    /// <summary>The text was read.</summary>
    None,
    /// <summary>No text in it: a scan, images, or fonts that carry no characters.</summary>
    NoText,
    /// <summary>It opens only with a password.</summary>
    Encrypted,
    /// <summary>Its structure could not be read (damaged, or a form this reader does not know).</summary>
    Unreadable,
    /// <summary>Reading it took longer than <see cref="DocumentText.Deadline"/>.</summary>
    TooSlow,
    /// <summary>Larger than <see cref="DocumentText.MaxBytes"/>: not read.</summary>
    TooLarge,
}

/// <summary>A document that would take more than a reader may spend — memory, a page count, a text length: reading
/// stops, said as too large. Never an <see cref="InvalidDataException"/>, which readers take for a damaged part.</summary>
internal sealed class DocumentLimitException(string message) : Exception(message);

/// <summary>The text of a document, read on this machine.</summary>
/// <param name="Text">What was read; empty when nothing was.</param>
/// <param name="Pages">Pages in a PDF; 0 for a DOCX.</param>
/// <param name="PagesWithoutText">PDF pages that gave no text (images, scans) while others did.</param>
/// <param name="Problem">Why no text, or <see cref="DocumentProblem.None"/>.</param>
/// <param name="StoppedAt">Reading stopped at a limit (<see cref="PdfText.MaxPages"/> pages, <see cref="PdfText.MaxChars"/>
/// characters): the pages read, the rest left out and said; 0 when the whole document was read.</param>
internal sealed record DocumentExtraction(string Text, int Pages, int PagesWithoutText, DocumentProblem Problem, int StoppedAt = 0);

/// <summary>
/// Reads the text of a PDF or a DOCX the person attaches, on this machine, with no package: a DOCX is a zip of XML, a PDF
/// is read by <see cref="PdfText"/>. Bounded in time: a document that takes longer is said, never waited for.
/// </summary>
internal static class DocumentText
{
    /// <summary>How long one document may take to read.</summary>
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);

    /// <summary>The largest document read, in bytes: a PDF holds images, so the attachment limit of a text file is too small.</summary>
    internal const long MaxBytes = 20L * 1024 * 1024;

    /// <summary>Whether <paramref name="path"/> is a document this reader extracts text from, by its extension.</summary>
    internal static bool Handles(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".pdf" or ".docx";

    /// <summary>The text of the PDF or DOCX at <paramref name="path"/>.</summary>
    internal static DocumentExtraction Extract(string path, CancellationToken ct = default)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(string.Empty, 0, 0, DocumentProblem.Unreadable); }
        return Extract(bytes, Path.GetExtension(path), ct);
    }

    /// <summary>The text of a document held in <paramref name="bytes"/>, by its <paramref name="extension"/>.</summary>
    /// <param name="ct">The person's cancel (Stop): it propagates; the reader's own deadline is said as too slow.</param>
    internal static DocumentExtraction Extract(byte[] bytes, string extension, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Deadline);
        try
        {
            return extension.ToLowerInvariant() switch
            {
                ".docx" => DocxText.Extract(bytes, deadline.Token),
                _       => PdfText.Extract(bytes, deadline.Token),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(string.Empty, 0, 0, DocumentProblem.TooSlow); }
        catch (DocumentLimitException ex)
        {
            Diagnostics.Swallow("DocumentText.Limit", ex);
            return new(string.Empty, 0, 0, DocumentProblem.TooLarge);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or IndexOutOfRangeException
                                       or ArgumentException or InvalidOperationException or OverflowException
                                       or System.Xml.XmlException or System.Security.Cryptography.CryptographicException)
        {
            Diagnostics.Swallow("DocumentText.Extract", ex);
            return new(string.Empty, 0, 0, DocumentProblem.Unreadable);
        }
    }
}
