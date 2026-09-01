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
    public string Description => "Reads the full content of a text file.";
    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", description = "Absolute path to the file." }
        },
        required = new[] { "path" }
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var path = PathSanitizer.Sanitize(args.Str("path"));
        PathSanitizer.AssertUnderRoot(path, _getWorkspaceRoot());

        // Dirty-buffer overlay first: an open (possibly unsaved, possibly not-yet-created)
        // document must be read as the user sees it, not as the disk last saved it.
        if (_overlay is not null && _overlay.TryGet(path, out var buffered))
            return buffered;

        if (!File.Exists(path))
            return Strings.ToolFileNotFound(path);

        return Cap(await File.ReadAllTextAsync(path, ct), path);
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
