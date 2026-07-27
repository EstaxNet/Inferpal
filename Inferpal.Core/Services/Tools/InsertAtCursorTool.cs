using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Editor;

namespace Inferpal.Services.Tools;

internal class InsertAtCursorTool : ITool
{
    private readonly IEditorSurface _editor;

    public InsertAtCursorTool(IEditorSurface editor) => _editor = editor;

    public string Name => "insert_at_cursor";

    public string Description =>
        "Inserts text at the caret position in the active editor. " +
        "The existing selection (if any) is not replaced — use replace_selection for that.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            text = new { type = "string", description = "Text to insert at the caret position." }
        },
        required = new[] { "text" },
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (!_editor.IsAvailable)
            return Strings.ActiveDocNoContext;

        if (!args.TryGetProperty("text", out var textEl) || textEl.ValueKind != JsonValueKind.String)
            return "Missing required parameter: text";
        var text = textEl.GetString()!;

        var path = await _editor.InsertAtCursorAsync(text, ct);
        if (path is null)
            return Strings.ActiveDocNoFile;

        return Strings.InsertOk(path, text.Length);
    }
}
