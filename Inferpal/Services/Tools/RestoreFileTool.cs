using System.Text.Json;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

internal class RestoreFileTool : ITool
{
    private readonly FileHistoryService _history;

    public RestoreFileTool(FileHistoryService history) => _history = history;

    public string Name        => "restore_file";
    public string Description =>
        "Restores a file from a previously saved backup snapshot in .inferpal/history/. " +
        "If no snapshot path is provided, uses the most recent backup. " +
        "Use this to undo changes made by write_file or apply_diff.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path     = new { type = "string", description = "Absolute path to the file to restore." },
            snapshot = new { type = "string", description = "(Optional) Absolute path to a specific snapshot file. If omitted, uses the most recent backup." }
        },
        required = new[] { "path" }
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var path = PathSanitizer.Sanitize(args.GetProperty("path").GetString());

        string? snapPath = null;
        if (args.TryGetProperty("snapshot", out var snapEl) && snapEl.ValueKind == JsonValueKind.String)
        {
            var snapRaw = snapEl.GetString();
            if (snapRaw is not null)
                snapPath = PathSanitizer.Sanitize(snapRaw);
        }

        snapPath ??= _history.FindMostRecentSnapshot(path);

        if (snapPath is null)
            return Strings.RestoreNotFound(path);

        await _history.RestoreAsync(snapPath, path, ct);
        return Strings.RestoreOk(path, snapPath);
    }
}
