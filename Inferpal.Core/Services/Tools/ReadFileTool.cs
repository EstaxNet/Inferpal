using System.IO;
using System.Text.Json;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

internal class ReadFileTool : ITool
{
    private readonly Func<string?> _getWorkspaceRoot;
    private readonly Editor.OpenDocumentOverlay? _overlay;

    /// <param name="overlay">Open-document mirror consulted before disk so unsaved buffer
    /// content wins; null when the editor feeds no overlay (VS in-proc today).</param>
    public ReadFileTool(Func<string?> getWorkspaceRoot, Editor.OpenDocumentOverlay? overlay = null)
    {
        _getWorkspaceRoot = getWorkspaceRoot;
        _overlay          = overlay;
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

        // An unsaved (possibly not-yet-created) buffer must be read as the user sees it, not as the
        // disk last saved it. A saved one is read from disk: the file may have just been written.
        string content;
        if (_overlay is not null && _overlay.TryGetUnsaved(path, out var buffered))
            content = buffered;
        // ⚠ A directory is not a missing file: "not found" sent the model looking for a path that is correct.
        else if (Directory.Exists(path))
            return $"'{path}' is a directory, not a file — list what it holds with list_files.";
        else if (!File.Exists(path))
            return Strings.ToolFileNotFound(path);
        // Named, not dumped: read as text, a .dll was thousands of characters of NULs and noise read as content.
        else if (TextFileEncoding.IsBinaryFile(path))
            return $"'{Path.GetFileName(path)}' is a binary file ({new FileInfo(path).Length} bytes): its content is not "
                 + "shown as text.";
        else
            content = Cap(await TextFileEncoding.ReadTextAsync(path, ct), path);

        return Page(content, Path.GetFileName(path), args.Int("start_line", 0), args.Int("end_line", 0));
    }

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
    internal static string Page(string content, string name, int start, int end)
    {
        var ranged = start > 0 || end > 0;
        if (!ranged && content.Length <= PageChars) return content;

        var lines = content.Split('\n');
        // A trailing newline ends the last line; it does not open another.
        var total = content.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        if (total <= 0) return content;

        var first = Math.Max(1, start);
        if (first > total)
            return $"[start_line {first} is past the end: {name} has {total} lines]";
        var last = end > 0 ? Math.Min(end, total) : total;
        if (last < first)
            return $"[end_line {end} is before start_line {first}]";

        var shown = first - 1;
        var size  = 0;
        for (var i = first; i <= last; i++)
        {
            var add = lines[i - 1].Length + 1;
            // With no end asked for, whole lines up to the page — at least one, even an over-long one.
            if (end <= 0 && shown >= first && size + add > PageChars) break;
            size += add;
            shown = i;
        }

        if (first == 1 && shown == total) return content;

        var body = string.Join('\n', lines[(first - 1)..shown]);
        if (shown < total || content.EndsWith('\n')) body += "\n";

        return shown < total
            ? body + $"[{name}: lines {first}–{shown} of {total} shown — call read_file with start_line={shown + 1} to read on]"
            : body + $"[{name}: lines {first}–{total} of {total} — the end of the file]";
    }

    /// <summary>
    /// Ceiling on what one <c>read_file</c> hands back. Generous — two orders of magnitude above
    /// any source file, and above the 200 KB the indexer itself refuses to chunk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The agent loop caps what reaches the <em>context</em> (<c>MaxToolResultCharsInContext</c>),
    /// but that happens after the whole file is a string: reading a multi-hundred-megabyte file —
    /// a database dump, a captured log, a bundled asset the model was curious about — materialised
    /// all of it, twice while it was copied, in the extension host.
    /// </para>
    /// <para>
    /// The cut is <b>announced to the model</b>, and it names the way out. Silently handing back a
    /// prefix would let it conclude a symbol is absent from a file it only read the start of.
    /// </para>
    /// </remarks>
    internal const int MaxChars = 2_000_000;

    private static string Cap(string content, string path) =>
        content.Length <= MaxChars
            ? content
            : SafeTruncate.Truncate(content, MaxChars)
              + $"\n\n[... {Path.GetFileName(path)} is {content.Length} characters; the first {MaxChars} "
              + "are shown. Use search_in_files to find what you need in the rest.]";
}
