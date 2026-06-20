using System.Text.Json;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;
using Inferpal.Localization;
using Inferpal.Services;

namespace Inferpal.Services.Tools;

internal class InsertAtCursorTool : ITool
{
    private readonly VisualStudioExtensibility _vs;
    private readonly VsContextHolder           _contextHolder;

    public InsertAtCursorTool(VisualStudioExtensibility vs, VsContextHolder contextHolder)
    {
        _vs            = vs;
        _contextHolder = contextHolder;
    }

    public string Name => "insert_at_cursor";

    public string Description =>
        "Inserts text at the caret position in the active Visual Studio editor. " +
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
        if (_contextHolder.Context is null)
            return Strings.ActiveDocNoContext;

        var textView = await _vs.Editor().GetActiveTextViewAsync(_contextHolder.Context, ct);
        if (textView is null)
            return Strings.ActiveDocNoFile;

        var text           = args.GetProperty("text").GetString()!;
        var insertionPoint = textView.Selection.InsertionPosition;
        var path           = textView.FilePath ?? textView.Document.Uri.LocalPath;

        await _vs.Editor().EditAsync(
            batch =>
            {
                var docEditor = textView.Document.AsEditable(batch);
                docEditor.Insert(insertionPoint, text);
            },
            ct);

        return Strings.InsertOk(path, text.Length);
    }
}
