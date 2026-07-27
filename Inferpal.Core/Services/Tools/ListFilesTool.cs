using System.IO;
using System.Text.Json;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

internal class ListFilesTool : ITool
{
    private readonly Func<string?> _getWorkspaceRoot;

    public ListFilesTool(Func<string?> getWorkspaceRoot) => _getWorkspaceRoot = getWorkspaceRoot;

    public string Name => "list_files";
    public string Description => "Recursively lists files in a directory.";
    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path    = new { type = "string", description = "Directory path." },
            pattern = new { type = "string", description = "Glob filter, e.g. *.cs (default: *)" }
        },
        required = new[] { "path" }
    };

    public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var path    = PathSanitizer.Sanitize(args.GetProperty("path").GetString());
        PathSanitizer.AssertUnderRoot(path, _getWorkspaceRoot());
        var pattern = args.TryGetProperty("pattern", out var p) ? p.GetString() ?? "*" : "*";

        if (!Directory.Exists(path))
            return Task.FromResult(Strings.DirNotFound(path));

        const int limit = 300;
        var all   = Directory.GetFiles(path, pattern, SearchOption.AllDirectories);
        var files = all.Take(limit).Select(f => f[path.Length..].TrimStart('\\', '/'));
        var result = string.Join("\n", files);

        if (all.Length > limit)
            result += $"\n(showing first {limit} of {all.Length} files)";

        return Task.FromResult(result);
    }
}
