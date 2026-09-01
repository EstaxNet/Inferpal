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
        var path    = PathSanitizer.Sanitize(args.Str("path"));
        PathSanitizer.AssertUnderRoot(path, _getWorkspaceRoot());
        var pattern = args.TryGetProperty("pattern", out var p) ? p.GetString() ?? "*" : "*";

        if (!Directory.Exists(path))
            return Task.FromResult(Strings.DirNotFound(path));

        // Lazy + excluded like the semantic index: on a node project root, GetFiles materialised
        // the whole tree and the 300 results shown were mostly node_modules/.git noise (the
        // pre-1.6.0 architecture review). Take(limit + 1) detects truncation without walking everything.
        const int limit = 300;
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(path, pattern, SearchOption.AllDirectories)
                             .Where(f => !WorkspaceScan.IsExcludedPath(f))
                             .Take(limit + 1)
                             .Select(f => f[path.Length..].TrimStart('\\', '/'))
                             .ToList();
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("ListFilesTool.Enumerate", ex);
            return Task.FromResult(Strings.DirNotFound(path));
        }

        var truncated = files.Count > limit;
        if (truncated) files.RemoveAt(files.Count - 1);
        var result = string.Join("\n", files);
        if (truncated)
            result += $"\n(showing first {limit} files — narrow the path or pattern for the rest)";

        return Task.FromResult(result);
    }
}
