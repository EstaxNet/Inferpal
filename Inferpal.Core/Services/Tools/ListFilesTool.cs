using System.IO;
using System.Text.Json;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

internal class ListFilesTool : ITool
{
    private readonly Func<string?> _getWorkspaceRoot;

    public ListFilesTool(Func<string?> getWorkspaceRoot) => _getWorkspaceRoot = getWorkspaceRoot;

    public string Name => "list_files";
    /// <inheritdoc/>
    public ToolEffect EffectOf(JsonElement args) => ToolEffect.Reads;
    public string Description => "Recursively lists files in a directory, or the entries of an archive (zip, nupkg, vsix, jar, tar, tar.gz).";
    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path    = new { type = "string", description = "Directory path, or an archive (logs.zip, or logs.zip!/folder)." },
            pattern = new { type = "string", description = "Glob filter, e.g. *.cs (default: *)" }
        },
        required = new[] { "path" }
    };

    public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var root    = _getWorkspaceRoot();
        var rawPattern = args.Str("pattern");
        if (WorkspaceScan.NormalizeFilePattern(rawPattern) is not { } pattern)
            return Task.FromResult(WorkspaceScan.InvalidPatternMessage("pattern", rawPattern));
        var inner = string.Empty;
        var raw = Documents.Archives.TrySplit(args.Str("path"), out var archivePart, out inner) ? archivePart : args.Str("path");
        var path    = PathSanitizer.Sanitize(raw, root);
        PathSanitizer.AssertUnderRoot(path, root);
        if (File.Exists(path) && Documents.Archives.Handles(path))
            return Task.Run(() => ListArchive(path, inner, pattern, root, ct), ct);

        // A file is not a missing directory: "Directory not found" for a path that exists read "it does not exist".
        if (File.Exists(path))
            return Task.FromResult($"'{path}' is a file, not a directory — read it with read_file.");
        if (!Directory.Exists(path))
            return Task.FromResult(Strings.DirNotFound(path));

        // Lazy + excluded like the semantic index: on a node project root, GetFiles materialises
        // the whole tree and the 300 results shown are mostly node_modules/.git noise.
        // Take(limit + 1) detects truncation without walking everything.
        const int limit = 300;
        List<string> files;
        // The walk is checked BEFORE it is consumed: "the directory does not exist" was answered
        // for a directory that exists and could not be opened, which sends the reader to verify a
        // path that is perfectly correct.
        var walk = WorkspaceScan.EnumerateFiles(path, pattern, out var walkFailed);
        if (walkFailed)
            return Task.FromResult($"Could not list '{path}': the directory exists but could not be "
                                 + "walked (permissions, or a path the file system refused).");
        try
        {
            // ⚠ Sorted BEFORE the cap: the message below says "showing first {limit} files", and
            // "first" has to mean something. The walk yields in file-system order — by name on
            // NTFS, arbitrary on POSIX — so without this the listing shown to the model was a
            // subset chosen by the volume, different between two identical calls. Ordinal, like
            // ScanCoverage.Take, which owns the same decision for the capped analysis scans.
            files = walk.OrderBy(f => f, StringComparer.Ordinal)
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
        // What reaches the model is what fits its context: past that the loop would cut the MIDDLE of the listing.
        var shown = ToolOutputBudget.LinesThatFit(files);
        // An empty listing is SAID: an empty tool result tells the model nothing, not even that nothing matched.
        var result = files.Count == 0 ? Strings.NoResults : string.Join("\n", files.Take(shown));
        if (shown < files.Count)
            result += $"\n(showing the first {shown} of {files.Count}{(truncated ? "+" : "")} files — the rest would not "
                    + "fit the context; narrow the path or pattern for them)";
        else if (truncated)
            result += $"\n(showing first {limit} files — narrow the path or pattern for the rest)";
        // ⚠ The cap is not the only reason this listing is partial. A folder the walk could not list,
        // or one it will not follow because it is a link, contributes NOTHING and is absent from the
        // output altogether — which reads as "that folder is empty", or as "it does not exist". The
        // walk's `failed` flag only ever meant "the START directory could not be opened".
        if (WorkspaceScan.FirstWalkGap(path, root) is { } gap)
            result += $"\n({gap.Sentence()})";

        return Task.FromResult(result);
    }
    /// <summary>The entries of an archive (or of a folder in it), as a listing: sorted, the ignore files judging each entry as
    /// if the archive were a folder, cut to what one result holds and the rest counted.</summary>
    private static string ListArchive(string archive, string inner, string pattern, string? root, CancellationToken ct)
    {
        var name = Path.GetFileName(archive);
        if (Governance.ContentExclusions.Of(archive) is { } excluded) return excluded.ForModel(Governance.ContentExclusions.Shown(archive, root));
        var (entries, problem) = Documents.Archives.List(archive, inner, out var more, out var oddNames, ct);
        if (entries is null) return $"'{name}' cannot be listed: {problem}.";
        var expression = System.IO.Enumeration.FileSystemName.TranslateWin32Expression(pattern);
        var kept = new List<string>();
        var hidden = 0;
        foreach (var e in entries)
        {
            if (!System.IO.Enumeration.FileSystemName.MatchesWin32Expression(expression, e.Path.AsSpan(e.Path.LastIndexOf('/') + 1), ignoreCase: true)) continue;
            if (Documents.Archives.ExcludedEntry(archive, e.Path)) { hidden++; continue; }
            kept.Add($"{name}!/{e.Path}  ({e.Size} bytes)");
        }
        var where = inner.Length > 0 ? $"{name}!/{inner}" : name;
        if (kept.Count == 0) return $"No entry of {where} matches{(hidden > 0 ? $" ({hidden} excluded by the ignore files)" : string.Empty)}.";
        var fit = ToolOutputBudget.LinesThatFit(kept);
        var report = $"{kept.Count} entr{(kept.Count == 1 ? "y" : "ies")} in {where} — read one with read_file (path='{name}!/<entry>'):\n"
                   + string.Join('\n', kept.Take(fit));
        if (fit < kept.Count) report += $"\n… +{kept.Count - fit} more not listed: name a folder of the archive (path='{name}!/folder') or a pattern.";
        if (hidden > 0) report += $"\n({hidden} entr{(hidden == 1 ? "y" : "ies")} excluded by the ignore files.)";
        if (oddNames > 0) report += $"\n({oddNames} entr{(oddNames == 1 ? "y" : "ies")} left out: a name no honest archive holds — absolute, a drive, '..', control or direction characters.)";
        if (more) report += $"\n(Listing stopped at {Documents.Archives.MaxEntries} entries.)";
        return report;
    }
}
