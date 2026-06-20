using System.IO;
using System.Text.Json;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

internal class ReadFileTool : ITool
{
    private readonly Func<string?> _getWorkspaceRoot;

    public ReadFileTool(Func<string?> getWorkspaceRoot) => _getWorkspaceRoot = getWorkspaceRoot;

    public string Name => "read_file";
    public string Description => "Reads the full content of a text file.";
    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", description = "Absolute path to the file." }
        },
        required = new[] { "path" }
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var path = PathSanitizer.Sanitize(args.GetProperty("path").GetString());
        PathSanitizer.AssertUnderRoot(path, _getWorkspaceRoot());

        if (!File.Exists(path))
            return Strings.ToolFileNotFound(path);

        return await File.ReadAllTextAsync(path, ct);
    }
}
