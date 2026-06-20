using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;

namespace Inferpal.Services.Tools;

internal class GetActiveDocumentTool : ITool
{
    private readonly VisualStudioExtensibility _vs;
    private readonly VsContextHolder           _contextHolder;

    public GetActiveDocumentTool(VisualStudioExtensibility vs, VsContextHolder contextHolder)
    {
        _vs            = vs;
        _contextHolder = contextHolder;
    }

    public string Name => "get_active_document";

    public string Description =>
        "Returns the path and full content of the file currently open " +
        "in the Visual Studio editor. Takes no parameters.";

    public object Parameters => new
    {
        type = "object",
        properties = new { },
        required = Array.Empty<string>(),
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (_contextHolder.Context is null)
            return Strings.ActiveDocNoContext;

        var textView = await _vs.Editor().GetActiveTextViewAsync(_contextHolder.Context, ct);
        if (textView is null)
            return Strings.ActiveDocNoFile;

        var path    = textView.Document.Uri.LocalPath;
        var content = textView.Document.Text.CopyToString();

        return Strings.ActiveDocResult(path, content);
    }
}
