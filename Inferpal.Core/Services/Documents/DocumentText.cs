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
/// <param name="Header">What the model is told the text is, in place of the line a document gets (an event log is not a
/// document whose formatting and images were left out); it then says any cut itself.</param>
internal sealed record DocumentExtraction(string Text, int Pages, int PagesWithoutText, DocumentProblem Problem, int StoppedAt = 0,
                                          string? Header = null);

/// <summary>
/// Reads the text of a PDF or a DOCX the person attaches, on this machine, with no package: a DOCX is a zip of XML, a PDF
/// is read by <see cref="PdfText"/>. Bounded in time: a document that takes longer is said, never waited for.
/// </summary>
internal static class DocumentText
{
    /// <summary>
    /// <paramref name="read"/> on a thread of its own, waited for at most <paramref name="limit"/> (and the person's Stop): past
    /// it the caller goes on, said as too slow, and the thread finishes alone — bounded by <see cref="MaxBytes"/>.
    /// </summary>
    private static DocumentExtraction RunWithin(Func<DocumentExtraction> read, TimeSpan limit, CancellationToken ct)
    {
        DocumentExtraction? result = null;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try { result = read(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true, Name = "Inferpal document reader" };
        worker.Start();
        var until = DateTime.UtcNow + limit + TimeSpan.FromMilliseconds(250);
        while (!worker.Join(50))
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > until) return new(string.Empty, 0, 0, DocumentProblem.TooSlow);
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result!;
    }

    /// <summary>The bytes as text when they read as one (UTF-8, or a BOM's encoding) with no control characters but line
    /// breaks and tabs; an HTML table loses its tags. Null for anything else.</summary>
    private static string? PlainText(byte[] bytes)
    {
        string text;
        try { text = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? bytes[3..] : bytes); }
        catch (System.Text.DecoderFallbackException) { return null; }
        if (text.Any(c => c < ' ' && c is not ('\n' or '\r' or '\t'))) return null;
        return text.TrimStart().StartsWith('<') ? Tools.FetchUrlTool.HtmlToText(text) : text;
    }

    /// <summary>How long one document may take to read.</summary>
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);

    /// <summary>How long a document the person typed the password of may take: its key derivation alone (LibreOffice's
    /// Argon2id, 64 MiB and 3 passes; Office's 100 000 hash rounds) can pass <see cref="Deadline"/> on a slow machine, and the
    /// person is waiting for this file, not reading many.</summary>
    internal static readonly TimeSpan PasswordDeadline = TimeSpan.FromSeconds(10);

    /// <summary>The largest document read, in bytes: a PDF holds images, so the attachment limit of a text file is too small.</summary>
    internal const long MaxBytes = 20L * 1024 * 1024;

    /// <summary>The largest file of the kind <paramref name="path"/> names that is read: an event log at Windows' default size
    /// (20 MiB of records and its headers) passes <see cref="MaxBytes"/>, and its text is bounded apart (the newest events).</summary>
    internal static long MaxBytesOf(string path) =>
        Path.GetExtension(path).Equals(".evtx", StringComparison.OrdinalIgnoreCase) ? 64L * 1024 * 1024 : MaxBytes;

    /// <summary>How long an event log may take: tens of thousands of events at a few per microsecond, read whole for its
    /// newest ones.</summary>
    internal static readonly TimeSpan EventLogDeadline = TimeSpan.FromSeconds(10);

    /// <summary>How long reading the file <paramref name="path"/> names may take, without a password.</summary>
    internal static TimeSpan DeadlineOf(string path) =>
        Path.GetExtension(path).Equals(".evtx", StringComparison.OrdinalIgnoreCase) ? EventLogDeadline : Deadline;

    /// <summary>The extensions of the documents this reader extracts text from. ⚠ VS Code sends exactly these to the host
    /// (<c>readAttachable</c>): <c>DocumentTextTests</c> holds the two lists equal.</summary>
    internal static readonly string[] Extensions =
        [".pdf", ".docx", ".xlsx", ".xlsm", ".xlsb", ".xls", ".pptx", ".ipynb", ".odt", ".ods", ".odp", ".rtf", ".eml", ".msg", ".doc",
         ".evtx", ".drawio", ".vsdx"];

    /// <summary>Whether <paramref name="path"/> is a document this reader extracts text from, by its extension.</summary>
    internal static bool Handles(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>The documents that are text files themselves (a notebook's JSON, RTF, a MIME e-mail, a draw.io diagram's
    /// XML): attached, they are read as their content; the agent's read_file and search_in_files take them as they are, so
    /// an edit quotes what is in the file and write_file knows it was read.</summary>
    internal static bool IsTextFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".ipynb" or ".rtf" or ".eml" or ".drawio";

    /// <summary>Whether the agent's tools read <paramref name="path"/> through this reader rather than as a text file.</summary>
    internal static bool ToolsRead(string path) => Handles(path) && !IsTextFile(path);

    /// <summary>The text of the PDF or DOCX at <paramref name="path"/>.</summary>
    /// <param name="password">The password the person typed for a protected PDF or DOCX.</param>
    internal static DocumentExtraction Extract(string path, CancellationToken ct = default, string? password = null)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(string.Empty, 0, 0, DocumentProblem.Unreadable); }
        return Extract(bytes, Path.GetExtension(path), ct, password);
    }

    /// <summary>The text of a document held in <paramref name="bytes"/>, by its <paramref name="extension"/>.</summary>
    /// <param name="ct">The person's cancel (Stop): it propagates; the reader's own deadline is said as too slow.</param>
    /// <param name="password">The password the person typed for a protected PDF or DOCX.</param>
    internal static DocumentExtraction Extract(byte[] bytes, string extension, CancellationToken ct = default, string? password = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var limit = password is not null ? PasswordDeadline : DeadlineOf(extension);
        deadline.CancelAfter(limit);
        // ⚠ The content before the extension: WordPad and many exports save RTF as .doc, web apps save an HTML or text table
        // as .xls. Read by the compound-file readers they were "damaged"; they are read as what they are.
        var kind = extension.ToLowerInvariant();
        if (kind is ".doc" or ".xls" && !OfficeCrypto.IsCompoundFile(bytes))
        {
            if (bytes.AsSpan().StartsWith("{\\rtf"u8)) kind = ".rtf";
            else if (PlainText(bytes) is { } plain)
                return plain.Trim().Length == 0 ? new(string.Empty, 0, 0, DocumentProblem.NoText)
                     : new(plain.Length > PdfText.MaxChars ? plain[..PdfText.MaxChars] : plain, 0, 0, DocumentProblem.None,
                           plain.Length > PdfText.MaxChars ? 1 : 0);
        }
        try
        {
            // ⚠ Run apart and waited for at most the deadline: several readers (Word 97, .xls, .msg) cannot be interrupted,
            // and a search reads documents one after the other — a stalled one would stall it whole.
            return RunWithin(() => kind switch
            {
                ".docx" => DocxText.Extract(bytes, deadline.Token, password),
                ".xlsx" or ".xlsm" or ".xlsb" or ".xls" => SpreadsheetText.Extract(bytes, deadline.Token, password),
                ".pptx" => PresentationText.Extract(bytes, deadline.Token, password),
                ".ipynb" => NotebookText.Extract(bytes, deadline.Token),
                ".odt" or ".ods" or ".odp" => OdfText.Extract(bytes, deadline.Token, password),
                ".rtf" => RtfText.Extract(bytes, deadline.Token),
                ".doc" => DocText.Extract(bytes, deadline.Token, password),
                ".eml" or ".msg" => EmailText.Extract(bytes, kind, deadline.Token),
                ".evtx" => EvtxText.Extract(bytes, deadline.Token),
                ".drawio" or ".vsdx" => DiagramText.Extract(bytes, kind, deadline.Token),
                _       => PdfText.Extract(bytes, deadline.Token, password),
            }, limit, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(string.Empty, 0, 0, DocumentProblem.TooSlow); }
        catch (DocumentLimitException ex)
        {
            Diagnostics.Swallow("DocumentText.Limit", ex);
            return new(string.Empty, 0, 0, DocumentProblem.TooLarge);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or IndexOutOfRangeException
                                       or ArgumentException or InvalidOperationException or OverflowException
                                       or System.Xml.XmlException or System.Security.Cryptography.CryptographicException
                                       or System.Text.Json.JsonException or IOException or NotSupportedException)
        {
            Diagnostics.Swallow("DocumentText.Extract", ex);
            return new(string.Empty, 0, 0, DocumentProblem.Unreadable);
        }
    }
}
