using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Editor;
using Inferpal.Services.Execution;

namespace Inferpal.Services.Tools;

internal class ReplaceSelectionTool : ITool
{
    private readonly IEditorSurface _editor;
    private readonly IApprovalService _approval;
    private readonly FileHistoryService _history;

    public ReplaceSelectionTool(IEditorSurface editor, IApprovalService approval, FileHistoryService history)
    {
        _editor   = editor;
        _approval = approval;
        _history  = history;
    }

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
        if (!args.TryGetProperty("text", out var textEl) || textEl.ValueKind != JsonValueKind.String)
            return "Missing required parameter: text";
        var text = textEl.GetString()!;

        // This one DESTROYS the selected text, so the prompt is the more load-bearing of the two —
        // see EditorWriteGate for what running without it bypassed.
        var gate = await EditorWriteGate.AuthorizeAsync(_editor, _approval, _history, Name, text, ct);
        if (!gate.MayProceed) return gate.Refusal!;

        var result = await _editor.ReplaceSelectionAsync(text, ct);
        if (result is null)
            return Strings.ActiveDocNoFile;

        return result.ReplacedSelection
            ? Strings.ReplaceOk(result.Path, text.Length)
            : Strings.InsertOk(result.Path, text.Length);
    }
}
