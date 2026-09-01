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
        var path        = PathSanitizer.Sanitize(args.Str("path"));
        PathSanitizer.AssertUnderRoot(path, _getWorkspaceRoot());
        var search      = args.Str("pattern") ?? throw new ArgumentException("pattern is required.");
        var filePattern = args.TryGetProperty("file_pattern", out var fp) ? fp.GetString() ?? "*" : "*";

        if (!Directory.Exists(path))
            return Task.FromResult(Strings.DirNotFound(path));

        Regex regex;
        try { regex = new Regex(search, RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexBudget.Default); }
        catch { regex = new Regex(Regex.Escape(search), RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexBudget.Default); }

        var results = new List<string>();

        // Lazy enumeration + the same artefact exclusions as the semantic index: GetFiles used to
        // materialise the whole tree (long seconds on a node project) and happily searched .git/,
        // node_modules/, bin/, obj/ — and the 100-result cap was only checked per FILE, so a
        // single minified file could add tens of thousands of lines (pre-1.6.0 architecture review).
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(path, filePattern, SearchOption.AllDirectories)
                             .Where(f => !WorkspaceScan.IsExcludedPath(f));
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("SearchInFilesTool.Enumerate", ex);
            return Task.FromResult(Strings.NoResults);
        }

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;
            if (results.Count >= 100) break;

            try
            {
                var lines = File.ReadAllLines(file);
                var relPath = file[path.Length..].TrimStart('\\', '/');
                for (int i = 0; i < lines.Length && results.Count < 100; i++)
                {
                    if (!regex.IsMatch(lines[i])) continue;
                    var line = lines[i].Trim();
                    if (line.Length > 400) line = line[..400] + "…";   // a minified line is not a result
                    results.Add($"{relPath}:{i + 1}: {line}");
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
