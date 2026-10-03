using System.IO;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Editor;

namespace Inferpal.Services.Tools;

internal class GetActiveDocumentTool : ITool
{
    private readonly IEditorSurface _editor;

    public GetActiveDocumentTool(IEditorSurface editor) => _editor = editor;

    public string Name => "get_active_document";

    public string Description =>
        "Returns the path and content of the file currently open in the editor — a long file comes back as its " +
        "first page, naming the start_line from which read_file reads on. Takes no parameters.";

    public object Parameters => new
    {
        type = "object",
        properties = new { },
        required = Array.Empty<string>(),
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (!_editor.IsAvailable)
            return Strings.ActiveDocNoContext;

        var doc = await _editor.GetActiveDocumentAsync(ct);
        if (doc is null)
            return Strings.ActiveDocNoFile;

        // ⚠ Paged like read_file: whole, a long file entered the context cut in its MIDDLE, under a marker that named
        // no way to read what was cut — while this description promised the full content.
        return Strings.ActiveDocResult(doc.Path, ReadFileTool.Page(doc.Text, Path.GetFileName(doc.Path), 0, 0));
    }
}
