using System.IO;
using System.Text.Json;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

internal class ReadFileTool : ITool
{
    private readonly Func<string?> _getWorkspaceRoot;
    private readonly Editor.OpenDocumentOverlay? _overlay;
    private readonly FileHistoryService? _history;

    /// <param name="overlay">Open-document mirror consulted before disk so unsaved buffer
    /// content wins; null when the editor feeds no overlay (VS in-proc today).</param>
    /// <param name="history">Told which files the model has read in the current run — what lets
    /// write_file replace an existing file (<see cref="FileTarget.UnreadRefusal"/>).</param>
    public ReadFileTool(Func<string?> getWorkspaceRoot, Editor.OpenDocumentOverlay? overlay = null,
                        FileHistoryService? history = null)
    {
        _getWorkspaceRoot = getWorkspaceRoot;
        _overlay          = overlay;
        _history          = history;
    }

    public string Name => "read_file";
    public string Description =>
        "Reads a text file. A file too long for one answer comes back in pages of whole lines, and the " +
        "answer says which start_line to pass to read on.";
    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path       = new { type = "string",  description = "Absolute path to the file." },
            start_line = new { type = "integer", description = "First line to read, 1-based (optional) — to continue a long file." },
            end_line   = new { type = "integer", description = "Last line to read, inclusive (optional)." },
        },
        required = new[] { "path" }
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var root = _getWorkspaceRoot();
        var path = PathSanitizer.Sanitize(args.Str("path"), root);
        PathSanitizer.AssertUnderRoot(path, root);

        string content;
        switch (Classify(path, _overlay))
        {
            // ⚠ A directory is not a missing file: "not found" sent the model looking for a path that is correct.
            case Target.Directory:
                return $"'{path}' is a directory, not a file — list what it holds with list_files.";
            case Target.Missing:
                return Strings.ToolFileNotFound(path);
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

        // An empty tool result says nothing — not even "empty": the model cannot tell it from a call that did nothing.
        if (content.Length == 0)
        {
            _history?.NoteRead(path);
            return $"[{Path.GetFileName(path)} is empty: 0 characters]";
        }

        var shown = PageOf(content, Path.GetFileName(path), args.Int("start_line", 0), args.Int("end_line", 0));
        // ⚠ What the model has SEEN, not what it asked for: a first page or a range is not the file, and write_file lets
        // a whole-file rewrite through only once every line of it has been in front of the model. A read cut at
        // MaxChars is not seen whole.
        if (shown.Text.Length > MaxChars) return Cap(shown.Text, Path.GetFileName(path));
        _history?.NoteReadLines(path, shown.First, shown.Last, shown.Total);
        return shown.Text;
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
