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
        var path = PathSanitizer.Sanitize(args.GetProperty("path").GetString());
        PathSanitizer.AssertUnderRoot(path, _getWorkspaceRoot());

        // Dirty-buffer overlay first: an open (possibly unsaved, possibly not-yet-created)
        // document must be read as the user sees it, not as the disk last saved it.
        if (_overlay is not null && _overlay.TryGet(path, out var buffered))
            return buffered;

        if (!File.Exists(path))
            return Strings.ToolFileNotFound(path);

        return await File.ReadAllTextAsync(path, ct);
    }
}
