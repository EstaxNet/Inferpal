using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Editor;
using Inferpal.Services.Execution;

namespace Inferpal.Services.Tools;

internal class InsertAtCursorTool : ITool
{
    private readonly IEditorSurface _editor;
    private readonly IApprovalService _approval;
    private readonly FileHistoryService _history;

    public InsertAtCursorTool(IEditorSurface editor, IApprovalService approval, FileHistoryService history)
    {
        _editor   = editor;
        _approval = approval;
        _history  = history;
    }

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
        if (!args.TryGetProperty("text", out var textEl) || textEl.ValueKind != JsonValueKind.String)
            return "Missing required parameter: text";
        var text = textEl.GetString()!;

        // Approval + snapshot, like every other tool that changes a file — see EditorWriteGate
        // for what this used to bypass.
        var gate = await EditorWriteGate.AuthorizeAsync(_editor, _approval, _history, Name, text, ct);
        if (!gate.MayProceed) return gate.Refusal!;

        var path = await _editor.InsertAtCursorAsync(text, ct);
        if (path is null)
            return Strings.ActiveDocNoFile;

        return Strings.InsertOk(path, text.Length);
    }
}
