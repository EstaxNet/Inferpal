using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Editor;

namespace Inferpal.Services.Tools;

internal class ReplaceSelectionTool : ITool
{
    private readonly IEditorSurface _editor;

    public ReplaceSelectionTool(IEditorSurface editor) => _editor = editor;

    public string Name => "replace_selection";

    public string Description =>
        "Replaces the current selection in the active editor with the given text. " +
        "If no text is selected, inserts at the caret position instead.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            text = new { type = "string", description = "Text to replace the selection with, or insert if nothing is selected." }
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

        var result = await _editor.ReplaceSelectionAsync(text, ct);
        if (result is null)
            return Strings.ActiveDocNoFile;

        return result.ReplacedSelection
            ? Strings.ReplaceOk(result.Path, text.Length)
            : Strings.InsertOk(result.Path, text.Length);
    }
}
