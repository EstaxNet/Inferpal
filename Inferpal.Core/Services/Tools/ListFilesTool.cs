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
        var root    = _getWorkspaceRoot();
        var path    = PathSanitizer.Sanitize(args.Str("path"), root);
        PathSanitizer.AssertUnderRoot(path, root);
        var rawPattern = args.Str("pattern");
        if (WorkspaceScan.NormalizeFilePattern(rawPattern) is not { } pattern)
            return Task.FromResult(WorkspaceScan.InvalidPatternMessage("pattern", rawPattern));

        if (!Directory.Exists(path))
            return Task.FromResult(Strings.DirNotFound(path));

        // Lazy + excluded like the semantic index: on a node project root, GetFiles materialised
        // the whole tree and the 300 results shown were mostly node_modules/.git noise (pre-1.6.0
        // review, batch 4). Take(limit + 1) detects truncation without walking everything.
        const int limit = 300;
        List<string> files;
        // The walk is checked BEFORE it is consumed: "the directory does not exist" was answered
        // for a directory that exists and could not be opened, which sends the reader to verify a
        // path that is perfectly correct.
        var walk = WorkspaceScan.EnumerateFiles(path, pattern, root, out var walkFailed);
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
        var result = string.Join("\n", files);
        if (truncated)
            result += $"\n(showing first {limit} files — narrow the path or pattern for the rest)";
        // ⚠ The cap is not the only reason this listing is partial. A folder the walk could not list,
        // or one it will not follow because it is a link, contributes NOTHING and is absent from the
        // output altogether — which reads as "that folder is empty", or as "it does not exist". The
        // walk's `failed` flag only ever meant "the START directory could not be opened".
        if (WorkspaceScan.FirstWalkGap(path, root) is { } gap)
            result += $"\n({gap.Sentence()})";

        return Task.FromResult(result);
    }
}
