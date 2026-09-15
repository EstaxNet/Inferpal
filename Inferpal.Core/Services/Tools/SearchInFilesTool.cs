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
        var root        = _getWorkspaceRoot();
        var path        = PathSanitizer.Sanitize(args.Str("path"), root);
        PathSanitizer.AssertUnderRoot(path, root);
        var search      = args.Str("pattern") ?? throw new ArgumentException("pattern is required.");
        var rawPattern  = args.Str("file_pattern");
        if (WorkspaceScan.NormalizeFilePattern(rawPattern) is not { } filePattern)
            return Task.FromResult(WorkspaceScan.InvalidPatternMessage("file_pattern", rawPattern));

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
        // ⚠ "No results" is a CONCLUSION the model acts on — it stops looking. A walk that could
        // not start is not that answer, and this catch used to return it anyway.
        var files = WorkspaceScan.EnumerateFiles(path, filePattern, root, out var walkFailed);
        if (walkFailed)
            return Task.FromResult(
                $"Could not search '{path}': the directory could not be walked (permissions, or a "
                + "path the file system refused). Nothing was searched — this is NOT \"the pattern "
                + "is absent from the code\".");

        var skippedLarge = 0;
        var unreadable   = 0;
        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;
            if (results.Count >= MaxResults) break;

            try
            {
                // A multi-megabyte file (a dump, a bundle) was loaded whole for a few truncated matches
                // at best. Skipped — and said, since a silent skip reads as "not in the code".
                if (new FileInfo(file).Length > MaxSearchFileBytes) { skippedLarge++; continue; }
                var lines = File.ReadAllLines(file);
                var relPath = file[path.Length..].TrimStart('\\', '/');
                for (int i = 0; i < lines.Length && results.Count < MaxResults; i++)
                {
                    if (!regex.IsMatch(lines[i])) continue;
                    var line = lines[i].Trim();
                    if (line.Length > 400) line = line[..400] + "…";   // a minified line is not a result
                    results.Add($"{relPath}:{i + 1}: {line}");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { unreadable++; Diagnostics.Swallow("SearchInFilesTool.ReadFile", ex); }
        }

        // Every reason the answer may be incomplete, said. The size skip already was; the RESULT
        // CAP was not, and it is the one that shapes a conclusion — a model asking "where is this
        // used?" reads exactly a hundred lines as the whole list and refactors on it.
        var notes = new System.Text.StringBuilder();
        if (results.Count >= MaxResults)
            notes.Append($"\n(stopped at the first {MaxResults} match(es) — narrow the path or the "
                       + "pattern to see the rest; this is NOT the complete list)");
        if (skippedLarge > 0)
            notes.Append($"\n({skippedLarge} file(s) larger than {MaxSearchFileBytes / (1024 * 1024)} MB were not searched.)");
        if (unreadable > 0)
            notes.Append($"\n({unreadable} file(s) could not be read and were not searched.)");

        return Task.FromResult((results.Count == 0 ? Strings.NoResults : string.Join("\n", results)) + notes);
    }

    /// <summary>Largest file read line by line — past it the file is skipped and counted.</summary>
    private const long MaxSearchFileBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Matches returned at most. Reaching it is SAID: a truncated list read as a complete one is how
    /// a model concludes a symbol is used in exactly a hundred places.
    /// </summary>
    internal const int MaxResults = 100;
}
