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
        "Returns the path and full content of the file currently open " +
        "in the editor. Takes no parameters.";

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

        return Strings.ActiveDocResult(doc.Path, doc.Text);
    }
}
