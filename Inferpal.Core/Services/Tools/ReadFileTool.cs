using System.IO;
using System.Text.Json;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

internal class ReadFileTool : ITool
{
    private readonly Func<string?> _getWorkspaceRoot;
    private readonly Editor.OpenDocumentOverlay? _overlay;
    private readonly FileHistoryService? _history;
    private readonly IApprovalService? _approval;
    private readonly Documents.ToolImageInbox? _images;

    /// <param name="overlay">Open-document mirror consulted before disk so unsaved buffer
    /// content wins; null when the editor feeds no overlay (VS in-proc today).</param>
    /// <param name="history">Told which files the model has read in the current run — what lets
    /// write_file replace an existing file (<see cref="FileTarget.UnreadRefusal"/>).</param>
    /// <param name="approval">Asks the person for a protected PDF's password, when a card can be drawn.</param>
    /// <param name="images">Where an image read is left for the loop to show the model; <c>null</c>: images are named, not
    /// read.</param>
    public ReadFileTool(Func<string?> getWorkspaceRoot, Editor.OpenDocumentOverlay? overlay = null,
                        FileHistoryService? history = null, IApprovalService? approval = null,
                        Documents.ToolImageInbox? images = null)
    {
        _images           = images;
        _approval         = approval;
        _getWorkspaceRoot = getWorkspaceRoot;
        _overlay          = overlay;
        _history          = history;
    }

    public string Name => "read_file";
    /// <inheritdoc/>
    public ToolEffect EffectOf(JsonElement args) => ToolEffect.Reads;
    public string Description =>
        "Reads a text file, or the text of a document (PDF, Word .docx/.doc, Excel .xlsx/.xls, PowerPoint .pptx, OpenDocument, Outlook .msg, .evtx event logs, .drawio/.vsdx diagrams); a notebook, an RTF or an .eml is read as the text file it is; an image (PNG, JPEG, WebP, GIF, BMP) is shown to you when you read images. A file too long for one answer comes back in pages of whole " +
        "lines, and the answer says which start_line to pass to read on. A log reads filtered by level and time (level, since, until): " +
        "only the matching entries, each line under its number.";
    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path       = new { type = "string",  description = "Absolute path to the file." },
            start_line = new { type = "integer", description = "First line to read, 1-based (optional) — to continue a long file." },
            end_line   = new { type = "integer", description = "Last line to read, inclusive (optional)." },
            level      = new { type = "string",  description = "A log: only entries at this level or above (optional).", @enum = LogFilter.LevelNames },
            since      = new { type = "string",  description = "A log: only entries at or after this time, \"HH:mm[:ss]\" or \"yyyy-MM-dd HH:mm[:ss]\" (optional)." },
            until      = new { type = "string",  description = "A log: only entries at or before this time, same forms (optional)." },
        },
        required = new[] { "path" }
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (LogRefusal(args) is { } refused) return refused;
        var root = _getWorkspaceRoot();
        // A real file whose name holds '!' is that file: the archive reading is for a path that names no file.
        if (Documents.Archives.TrySplit(args.Str("path"), out var archivePart, out var inner)
            && !File.Exists(PathSanitizer.Sanitize(args.Str("path"), root)))
            return await ReadArchiveEntryAsync(archivePart, inner, root, args, ct);
        var path = PathSanitizer.Sanitize(args.Str("path"), root);
        PathSanitizer.AssertUnderRoot(path, root);

        var target = Classify(path, _overlay);
        if (target is Target.File or Target.Binary && Documents.Archives.Handles(path))
            return $"'{Path.GetFileName(path)}' is an archive: list what it holds with list_files (path='{Path.GetFileName(path)}'), "
                 + $"and read an entry with read_file (path='{Path.GetFileName(path)}!/folder/file.txt').";
        // An image is seen, not read as text: the loop puts it in front of the model after the batch (ToolImages), or says
        // the model does not read images. Checked by its size before a byte is loaded; then by the attachments' one reader.
        if (target is Target.File or Target.Binary && _images is not null && Documents.ChatImages.Handles(path))
        {
            var name = Path.GetFileName(path);
            if (new FileInfo(path).Length > Documents.ChatImages.MaxBytes)
                return $"'{name}' is an image larger than {Documents.ChatImages.MaxBytes / (1024 * 1024)} MB: it is not shown.";
            var read = await Documents.ChatImages.ReadAsync(await File.ReadAllBytesAsync(path, ct), name, ct);
            if (read.Image is not { } image) return $"'{name}' is an image that is not shown: {read.Refusal}";
            _images.Put(image);
            var size = image.Width > 0 ? $", {image.Width}×{image.Height}" : string.Empty;
            return $"'{name}' is an image ({image.MediaType}{size}); it follows this result." + (read.Note is { } n ? $" {n}" : string.Empty);
        }
        // ⚠ A PDF or DOCX reads as its extracted text — checked before the binary test, which a PDF whose first bytes hold
        // no NUL passes, and which then decoded it as noise. Paged like any file, at the line numbers search_in_files
        // reports. Not noted as read: write_file must never take a document for a text it may rewrite whole.
        if (target is Target.File or Target.Binary && Documents.DocumentText.ToolsRead(path))
        {
            var name = Path.GetFileName(path);
            var extracted = await Task.Run(() => Documents.AttachedDocuments.ForModel(path, ct), ct);
            // A PDF that opens only with a password: the person, if at the keyboard, may open it — the model gets the text,
            // never the password, and never knows one was typed.
            if (extracted is null && _approval is { CanAskForInput: true } && Documents.AttachedDocuments.AsksForPassword(path))
            {
                if (!await _approval.AskForDocumentPasswordAsync(path, attached: false, ct))
                    return $"'{name}' stays closed: it opens only with a password, and it was not opened. Go on without "
                         + "it; do not ask the user for the password.";
                extracted = await Task.Run(() => Documents.AttachedDocuments.ForModel(path, ct), ct);
            }
            // A scan: this machine's OCR (every system, PdfOcr) — run once; when none can read it, the pages' pictures go to
            // a model that sees (pari 90), and a model that does not is told why and how to get an OCR.
            if (extracted is null && Documents.AttachedDocuments.IsScan(path))
            {
                var reading = await Task.Run(() => Documents.AttachedDocuments.Scan(path, ct), ct);
                if (reading.Status == Documents.OcrStatus.Read) extracted = Documents.AttachedDocuments.ScanText(name, reading);
                else if (reading.Status == Documents.OcrStatus.Empty)
                    return $"'{name}' is a scan, and the OCR ({reading.Engine}) found no text in it: a photo, a drawing or a blank page.";
                else return await ScanPicturesAsync(path, name, reading, ct);
            }
            if (extracted is null)
                return $"'{name}' gives no text to read: {Documents.AttachedDocuments.WhyNoText(path)}.";
            _history?.NoteOpened(path);
            if (Filtered(args)) return DocumentLogPage(extracted, name, args);
            var page = PageOf(extracted, name, args.Int("start_line", 0), args.Int("end_line", 0));
            return page.Text.Length > MaxChars ? Cap(page.Text, name) : page.Text;
        }

        string content;
        switch (target)
        {
            // ⚠ A directory is not a missing file: "not found" sent the model looking for a path that is correct.
            case Target.Directory:
                return $"'{path}' is a directory, not a file — list what it holds with list_files.";
            case Target.Missing:
                return ToolFailure.Said(Strings.ToolFileNotFound(path));
            // Named, not dumped: read as text, a .dll was thousands of characters of NULs and noise read as content.
            case Target.Binary:
                return $"'{Path.GetFileName(path)}' is a binary file ({new FileInfo(path).Length} bytes): its content is not "
                     + "shown as text.";
            case Target.Unsaved when _overlay!.TryGetUnsaved(path, out var buffered):
                content = buffered;
                break;
            default:
                content = await TextFileEncoding.ReadTextAsync(path, ct);
                break;
        }
        _history?.NoteOpened(path);

        // An empty tool result says nothing — not even "empty": the model cannot tell it from a call that did nothing.
        if (content.Length == 0)
        {
            _history?.NoteRead(path);
            return $"[{Path.GetFileName(path)} is empty: 0 characters]";
        }

        if (Filtered(args)) return LogPage(content, Path.GetFileName(path), args);
        // A test report reads first as its result, a draw.io diagram as its shapes and links; asked for lines, each is the
        // XML it holds.
        if (args.Int("start_line", 0) == 0 && args.Int("end_line", 0) == 0
            && (Documents.TestReportText.Summarize(Path.GetFileName(path), content, PageChars)
                ?? Documents.DiagramText.ForRead(Path.GetFileName(path), content, PageChars, ct)) is { } first)
            return first;
        var shown = PageOf(content, Path.GetFileName(path), args.Int("start_line", 0), args.Int("end_line", 0));
        // ⚠ What the model has SEEN, not what it asked for: a first page or a range is not the file, and write_file lets
        // a whole-file rewrite through only once every line of it has been in front of the model. A read cut at
        // MaxChars is not seen whole.
        if (shown.Text.Length > MaxChars) return Cap(shown.Text, Path.GetFileName(path));
        _history?.NoteReadLines(path, shown.First, shown.Last, shown.Total);
        return shown.Text;
    }

    /// <summary>
    /// An entry of an archive (<c>logs.zip!/app/log.txt</c>), read out in memory — never extracted —, paged like a file.
    /// </summary>
    /// <remarks>⚠ The ignore files judge the archive and each entry as if the archive were a folder: <c>.env</c> inside
    /// <c>backup.zip</c> is excluded where <c>.env</c> is. The registry's own check does not see this path (no such file on
    /// disk), so it is made here.</remarks>
    private async Task<string> ReadArchiveEntryAsync(string archivePart, string inner, string? root, JsonElement args, CancellationToken ct)
    {
        var archive = PathSanitizer.Sanitize(archivePart, root);
        PathSanitizer.AssertUnderRoot(archive, root);
        var name = Path.GetFileName(archive);
        if (Governance.ContentExclusions.Of(archive) is { } excluded)
            return excluded.ForModel(Governance.ContentExclusions.Shown(archive, root));
        if (inner.Length == 0 || inner.Split('/').Contains(".."))
            return $"Name an entry of '{name}': read_file path='{name}!/folder/file.txt'; list them with list_files path='{name}'.";
        var shown = $"{name}!/{inner}";
        if (Documents.Archives.Safe(inner) is null)
            return $"'{shown}' is not read: its name is not one an archive entry may have.";
        if (Documents.Archives.EntryExclusion(archive, inner) is { Excluded: true } entryExcluded)
            return entryExcluded.Why?.ForModel(shown) ?? $"'{shown}' is not read: it could not be judged against the workspace's ignore files.";
        if (Documents.Archives.Handles(inner))
            return $"'{shown}' is an archive inside an archive: it is not opened.";
        var (bytes, problem, locked) = await Task.Run(() => Documents.Archives.Read(archive, inner, ct), ct);
        // A protected entry: the person, if at the keyboard, may open it — the model gets the text, never the password.
        if (locked && _approval is { CanAskForInput: true })
        {
            // One card per archive at a time: two entries read in one batch would draw two cards for one password.
            var gate = ArchiveCards.GetOrAdd(archive, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                (bytes, problem, locked) = await Task.Run(() => Documents.Archives.Read(archive, inner, ct), ct);   // the other card may have opened it
                if (locked)
                {
                    if (!await _approval.AskForArchivePasswordAsync(archive, inner, ct))
                        return Documents.Archives.UnreadableAfterPassword.TryRemove((archive, inner), out var why)
                            ? $"'{shown}' cannot be read: {why}."
                            : $"'{shown}' stays closed: the archive opens only with a password, and it was not opened. Go on without "
                              + "it; do not ask the user for the password.";
                    (bytes, problem, locked) = await Task.Run(() => Documents.Archives.Read(archive, inner, ct), ct);
                }
            }
            finally { gate.Release(); }
        }
        if (locked) return $"'{shown}' is protected by a password: it was not opened (no one here to type it). Go on without it.";
        if (bytes is null) return $"'{shown}' cannot be read: {problem}.";
        // A document is read from a file of its own, never from inside an archive: said for what it is, not "binary".
        if (Documents.DocumentText.ToolsRead(inner))
            return $"'{shown}' is a document inside an archive: its text is read once it is extracted to a file of its own.";
        if (TextFileEncoding.IsBinary(bytes))
            return $"'{shown}' is a binary file ({bytes.Length} bytes): its content is not shown as text.";
        var text = TextFileEncoding.Decode(bytes);
        if (text.Length == 0) return $"[{shown} is empty: 0 characters]";
        if (Filtered(args)) return LogPage(text, shown, args);
        // A test report or a diagram in a CI artifact reads as it does on disk.
        if (args.Int("start_line", 0) == 0 && args.Int("end_line", 0) == 0
            && (Documents.TestReportText.Summarize(inner, text, PageChars) ?? Documents.DiagramText.ForRead(inner, text, PageChars, ct)) is { } first)
            return first;
        var page = PageOf(text, shown, args.Int("start_line", 0), args.Int("end_line", 0));
        return page.Text.Length > MaxChars ? Cap(page.Text, shown) : page.Text;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> ArchiveCards = new(PathComparer.Default);

    // A filter written empty ("level": "") is no filter: the plain read, never a selection of nothing.
    private static bool Filtered(JsonElement args) =>
        args.Keyword("level") is not null || args.Trimmed("since") is not null || args.Trimmed("until") is not null;

    /// <summary>A filter that cannot be read, named before anything is read; <c>null</c> when there is none or it reads.</summary>
    private static string? LogRefusal(JsonElement args)
    {
        if (args.Keyword("level") is { } level && !LogFilter.LevelNames.Contains(level))
            return $"Unknown level '{level}': pass one of {string.Join(", ", LogFilter.LevelNames)}.";
        foreach (var name in new[] { "since", "until" })
            if (args.Trimmed(name) is { } moment && LogFilter.ParseMoment(moment) is null)
                return $"'{moment}' is not a time {name} can take: pass \"HH:mm\", \"HH:mm:ss\" or \"yyyy-MM-dd HH:mm:ss\".";
        return null;
    }

    /// <summary>
    /// A log's entries that match, from <c>start_line</c>. Never noted as read: the model saw a selection, not the file —
    /// write_file must not take it for the whole.
    /// </summary>
    private static string LogPage(string text, string name, JsonElement args, int firstLine = 0) =>
        LogFilter.Apply(text, name, args.Keyword("level"), LogFilter.ParseMoment(args.Trimmed("since")),
                        LogFilter.ParseMoment(args.Trimmed("until")), args.Int("start_line", firstLine), args.Int("end_line", 0), PageChars);

    /// <summary>A document's text filtered as a log, the header Inferpal wrote above it said first.</summary>
    /// <remarks>⚠ The header is never judged as lines of the log: judged, it went under "lines before the first entry",
    /// and with it the only word that a long event log was cut to its newest events. Its lines keep their numbers, so a
    /// start_line from a plain read points at the same entry.</remarks>
    private static string DocumentLogPage(string extracted, string name, JsonElement args)
    {
        var end = extracted.StartsWith('[') ? extracted.IndexOf("\n\n", StringComparison.Ordinal) : -1;
        if (end < 0) return LogPage(extracted, name, args);
        var headerLines = extracted.AsSpan(0, end).Count('\n') + 2;
        return extracted[..end] + "\n" + LogPage(extracted, name, args, firstLine: headerLines + 1);
    }

    /// <summary>Pages of a scan shown when no OCR read it.</summary>
    internal const int MaxScanPictures = 5;

    /// <summary>
    /// A scan no OCR of this machine could read: its first pages' pictures (taken from the PDF, no renderer) go to a model
    /// that sees, after this result; the answer says why there is no OCR and how to get one.
    /// </summary>
    private async Task<string> ScanPicturesAsync(string path, string name, Documents.OcrReading reading, CancellationToken ct)
    {
        var why = $"'{name}' is a scan, and the OCR could not read it: {reading.Why ?? "the reading failed"}.";
        if (_images is null) return why;
        var extracted = await Task.Run(() => Documents.PdfPageImages.Extract(File.ReadAllBytes(path), MaxScanPictures, ct,
                                                                            Documents.AttachedDocuments.PasswordFor(path)), ct);
        var sent = 0;
        foreach (var picture in extracted.Images)
            if (Documents.ChatImages.Read(picture.Bytes, $"{name} page {picture.Page}{picture.Extension}").Image is { } image)
            {
                _images.Put(image);
                sent++;
            }
        if (sent == 0)
            return why;
        return why + $" The pictures of its first {sent} page(s){(extracted.Pages > sent ? $" of {extracted.Pages}" : "")} follow this result, to read them as images.";
    }

    /// <summary>What a path holds, as this tool reads it.</summary>
    internal enum Target { File, Unsaved, Directory, Missing, Binary }

    /// <summary>
    /// What <paramref name="path"/> (sanitised, under the root) holds — the one reading of it, for this tool and for
    /// <c>/read</c>, which attaches only what it actually read.
    /// </summary>
    /// <remarks>
    /// An unsaved (possibly not-yet-created) buffer is read as the user sees it, not as the disk last saved it; a saved
    /// one is read from disk, since the file may have just been written.
    /// </remarks>
    internal static Target Classify(string path, Editor.OpenDocumentOverlay? overlay) =>
        overlay is not null && overlay.TryGetUnsaved(path, out _) ? Target.Unsaved
        : Directory.Exists(path)                                   ? Target.Directory
        : !File.Exists(path)                                       ? Target.Missing
        : TextFileEncoding.IsBinaryFile(path)                      ? Target.Binary
        : Target.File;

    /// <summary>
    /// What one page may hold: every tool result enters the context cut to
    /// <see cref="Agent.AgentOrchestrator.MaxToolResultCharsInContext"/>, so a page under it is never cut
    /// there — with room for the footer.
    /// </summary>
    internal const int PageChars = Agent.AgentOrchestrator.MaxToolResultCharsInContext - 400;

    /// <summary>
    /// The lines asked for — or, with no range, the file itself when it fits one page and its first page
    /// otherwise, ending on the line to continue from.
    /// </summary>
    /// <remarks>
    /// ⚠ Without this the tool took no range: past the context cap the agent saw the beginning of a file,
    /// a marker saying how much was cut, and no way at all to read the rest — asking again returned the
    /// same beginning. A third of an ordinary repository's source files are that long. An explicit
    /// <paramref name="end"/> is honoured as asked (the loop still caps it, and says so); the whole range
    /// comes back exactly as the file holds it, which is what <c>/read</c> relies on to attach a file.
    /// </remarks>
    internal static string Page(string content, string name, int start, int end) => PageOf(content, name, start, end).Text;

    /// <summary>What one read shows of a file: the text, and the lines it covers — none (0, 0) when it shows none.</summary>
    internal readonly record struct Shown(string Text, int First, int Last, int Total);

    /// <summary><see cref="Page"/>, with the lines the page covers.</summary>
    /// <remarks>
    /// ⚠ Lines are found by index in the WHOLE text, never in a prefix: paged after a cut, the footer gave the prefix's
    /// line count as the file's, a line past it — one <c>search_in_files</c> had just reported — was "past the end", and
    /// the last page said "the end of the file" under the cut. And no copy of the file is split into lines: only the page.
    /// </remarks>
    internal static Shown PageOf(string content, string name, int start, int end)
    {
        var ranged = start > 0 || end > 0;
        var breaks = 0;
        foreach (var c in content) if (c == '\n') breaks++;
        // A trailing newline ends the last line; it does not open another.
        var total = content.EndsWith('\n') ? breaks : breaks + 1;
        if (!ranged && content.Length <= PageChars) return new(content, 1, total, total);
        if (total <= 0) return new(content, 1, total, total);

        var first = Math.Max(1, start);
        if (first > total)
            return new($"[start_line {first} is past the end: {name} has {total} lines]", 0, 0, total);
        var last = end > 0 ? Math.Min(end, total) : total;
        if (last < first)
            return new($"[end_line {end} is before start_line {first}]", 0, 0, total);

        var from = 0;
        for (var line = 1; line < first; line++) from = content.IndexOf('\n', from) + 1;

        var shown  = first - 1;
        var size   = 0;
        var cursor = from;
        for (var i = first; i <= last; i++)
        {
            var nl      = content.IndexOf('\n', cursor);
            var lineEnd = nl < 0 ? content.Length : nl;
            var add     = lineEnd - cursor + 1;
            // With no end asked for, whole lines up to the page — at least one, even an over-long one.
            if (end <= 0 && shown >= first && size + add > PageChars) break;
            size  += add;
            shown  = i;
            cursor = nl < 0 ? content.Length : nl + 1;
        }

        if (first == 1 && shown == total) return new(content, 1, total, total);

        // The lines shown, each with its own break: the last one has none only when it is the file's last.
        var body = content[from..cursor];
        if (shown < total && !body.EndsWith('\n')) body += "\n";

        return new(shown < total
            ? body + $"[{name}: lines {first}–{shown} of {total} shown — call read_file with start_line={shown + 1} to read on]"
            : body + $"[{name}: lines {first}–{total} of {total} — the end of the file]", first, shown, total);
    }

    /// <summary>
    /// Ceiling on what one <c>read_file</c> hands back — a range asked with an explicit <c>end_line</c>, which is
    /// returned as asked. Generous — two orders of magnitude above any source file.
    /// </summary>
    /// <remarks>
    /// The cut is <b>announced to the model</b>, and it names the way out. Silently handing back a
    /// prefix would let it conclude a symbol is absent from a range it only read the start of.
    /// </remarks>
    internal const int MaxChars = 2_000_000;

    private static string Cap(string read, string name) =>
        SafeTruncate.Truncate(read, MaxChars)
        + $"\n\n[... this read of {name} is {read.Length} characters; the first {MaxChars} are shown. Ask for a "
        + "narrower start_line/end_line range, or use search_in_files to find what you need.]";
}
