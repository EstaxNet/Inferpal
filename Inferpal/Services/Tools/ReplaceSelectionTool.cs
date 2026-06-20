using System.Text.Json;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;
using Inferpal.Localization;
using Inferpal.Services;

namespace Inferpal.Services.Tools;

internal class ReplaceSelectionTool : ITool
{
    private readonly VisualStudioExtensibility _vs;
    private readonly VsContextHolder           _contextHolder;

    public ReplaceSelectionTool(VisualStudioExtensibility vs, VsContextHolder contextHolder)
    {
        _vs            = vs;
        _contextHolder = contextHolder;
    }

    public string Name => "replace_selection";

    public string Description =>
        "Replaces the current selection in the active Visual Studio editor with the given text. " +
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
        if (_contextHolder.Context is null)
            return Strings.ActiveDocNoContext;

        var textView = await _vs.Editor().GetActiveTextViewAsync(_contextHolder.Context, ct);
        if (textView is null)
            return Strings.ActiveDocNoFile;

        var text      = args.GetProperty("text").GetString()!;
        var selection = textView.Selection;
        var path      = textView.FilePath ?? textView.Document.Uri.LocalPath;

        await _vs.Editor().EditAsync(
            batch =>
            {
                var docEditor = textView.Document.AsEditable(batch);
                if (selection.IsEmpty)
                    docEditor.Insert(selection.InsertionPosition, text);
                else
                    docEditor.Replace(new TextRange(selection.Start, selection.End), text);
            },
            ct);

        return selection.IsEmpty
            ? Strings.InsertOk(path, text.Length)
            : Strings.ReplaceOk(path, text.Length);
    }
}
