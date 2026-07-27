using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

internal class SearchInFilesTool : ITool
{
    private readonly Func<string?> _getWorkspaceRoot;

    public SearchInFilesTool(Func<string?> getWorkspaceRoot) => _getWorkspaceRoot = getWorkspaceRoot;

    public string Name => "search_in_files";
    public string Description => "Searches for text or a regex pattern in files. Returns file:line:content.";
    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path         = new { type = "string", description = "Root directory of the search." },
            pattern      = new { type = "string", description = "Text or regular expression to search for." },
            file_pattern = new { type = "string", description = "File filter, e.g. *.cs (default: *)" }
        },
        required = new[] { "path", "pattern" }
    };

    public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var path        = PathSanitizer.Sanitize(args.GetProperty("path").GetString());
        PathSanitizer.AssertUnderRoot(path, _getWorkspaceRoot());
        var search      = args.GetProperty("pattern").GetString()!;
        var filePattern = args.TryGetProperty("file_pattern", out var fp) ? fp.GetString() ?? "*" : "*";

        if (!Directory.Exists(path))
            return Task.FromResult(Strings.DirNotFound(path));

        Regex regex;
        try { regex = new Regex(search, RegexOptions.IgnoreCase | RegexOptions.Compiled); }
        catch { regex = new Regex(Regex.Escape(search), RegexOptions.IgnoreCase | RegexOptions.Compiled); }

        var results = new List<string>();

        foreach (var file in Directory.GetFiles(path, filePattern, SearchOption.AllDirectories))
        {
            if (ct.IsCancellationRequested) break;
            if (results.Count >= 100) break;

            try
            {
                var lines = File.ReadAllLines(file);
                var relPath = file[path.Length..].TrimStart('\\', '/');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (regex.IsMatch(lines[i]))
                        results.Add($"{relPath}:{i + 1}: {lines[i].Trim()}");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Diagnostics.Swallow("SearchInFilesTool.ReadFile", ex); }
        }

        return Task.FromResult(results.Count == 0
            ? Strings.NoResults
            : string.Join("\n", results));
    }
}
