using System.Text;
using System.Text.Json;

namespace Inferpal.Services.Tools;

internal class GetOpenEditorsTool : ITool
{
    private readonly VsContextHolder _contextHolder;

    public GetOpenEditorsTool(VsContextHolder contextHolder)
        => _contextHolder = contextHolder;

    public string Name => "get_open_editors";

    public string Description =>
        "Returns the list of files currently open in Visual Studio, with the active file clearly " +
        "identified. Use this to understand which files the user is currently working with, and to " +
        "provide context without asking the user to specify which file they are looking at.";

    public object Parameters => new
    {
        type       = "object",
        properties = new { },
        required   = Array.Empty<string>()
    };

    public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var openPaths  = _contextHolder.GetOpenPaths();
        var activePath = _contextHolder.LatestView?.Document.Uri.LocalPath;

        if (openPaths.Count == 0 && string.IsNullOrEmpty(activePath))
            return Task.FromResult(
                "No files are currently open in the editor. " +
                "The user may need to open a file first.");

        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(activePath))
            sb.AppendLine($"Active file: {activePath}");

        if (openPaths.Count > 0)
        {
            sb.AppendLine($"Open files ({openPaths.Count}):");
            foreach (var path in openPaths.OrderBy(p => p))
            {
                var active = string.Equals(path, activePath, StringComparison.OrdinalIgnoreCase);
                sb.AppendLine(active ? $"  * {path}  [active]" : $"    {path}");
            }
        }

        return Task.FromResult(sb.ToString().TrimEnd());
    }
}
