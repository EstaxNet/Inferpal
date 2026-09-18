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
        if (args.Str("text") is not { } text)
            return "Missing required parameter: text";

        // Approval + snapshot, like every other tool that changes a file — see EditorWriteGate
        // for what this used to bypass.
        var gate = await EditorWriteGate.AuthorizeAsync(_editor, _approval, _history, Name, text, ct);
        if (!gate.MayProceed) return gate.Refusal!;

        var path = await _editor.InsertAtCursorAsync(text, ct);
        // ⚠ Past the gate a file IS open — it just resolved one, and refuses without it. A null
        // here can therefore no longer mean "no file", it means "the edit did not apply": saying
        // otherwise makes this tool assert the opposite of what get_active_document answers on the
        // same session, and sends the reader looking for a file to open instead of a document that
        // changed or is read-only.
        if (path is null)
            return Strings.EditNotApplied(gate.Document!.Path);

        return Strings.InsertOk(path, text.Length);
    }
}
