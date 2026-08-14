using System.IO;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Rag;

/// <summary>
/// What the semantic index refuses to read: the built-in list (build output, VCS/IDE metadata,
/// Inferpal's own data directory) plus whatever <c>.inferpal/project.json</c> adds (roadmap §19).
/// Pure and testable — the indexing service owns the IO, this owns the decision.
/// </summary>
/// <remarks>
/// <b>Additive only.</b> The project profile can lengthen this list and has no way to shorten it:
/// the built-in names are checked first and no pattern can un-exclude a path. That is the same
/// shape as the deny-only permission overlay — a file that ships with a clone may restrict what
/// Inferpal does, never widen it.
/// </remarks>
internal static class IndexExclusions
{
    /// <summary>
    /// Directory names whose contents are never indexed: build artifacts, VCS/IDE metadata, and
    /// Inferpal's own data dir — <c>.inferpal/history/</c> holds snapshot COPIES of source files
    /// (same extensions), which would otherwise pollute the index with stale duplicates.
    /// </summary>
    public static readonly string[] BuiltInDirs =
        ["obj", "bin", ".git", "node_modules", ".vs", "dist", ".inferpal"];

    /// <summary>
    /// <c>true</c> when <paramref name="path"/> must stay out of the index.
    /// </summary>
    /// <param name="path">Absolute path of the candidate file.</param>
    /// <param name="root">Workspace root, used to make the path relative for pattern matching.</param>
    /// <param name="extra">
    /// Profile patterns. A pattern without a wildcard matches a whole path segment (so
    /// <c>vendor</c> excludes <c>vendor/</c> at any depth); otherwise the usual glob applies
    /// (<c>**</c>, <c>*</c>, <c>?</c>) against the path relative to the root.
    /// </param>
    public static bool IsExcluded(string path, string? root = null, IReadOnlyList<string>? extra = null)
    {
        foreach (var dir in BuiltInDirs)
        {
            if (path.Contains($@"\{dir}\", StringComparison.OrdinalIgnoreCase) ||
                path.Contains($"/{dir}/",  StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (extra is null || extra.Count == 0) return false;

        var rel = Relative(path, root);
        if (rel.Length == 0) return false;

        foreach (var pattern in extra)
        {
            var p = pattern.Replace('\\', '/').Trim().TrimStart('/').TrimEnd('/');
            if (p.Length == 0) continue;

            if (p.Contains('*') || p.Contains('?'))
            {
                if (GlobMatches(p, rel)) return true;
                // `vendor/**` is the same intent as `vendor` — but so is `vendor/*`, which the
                // glob would only match one level deep. Anchor the directory form explicitly.
                var prefix = p.TrimEnd('*').TrimEnd('/');
                if (prefix.Length > 0 && !prefix.Contains('*') && !prefix.Contains('?') &&
                    rel.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                    return true;
                continue;
            }

            // No wildcard: a plain name or path. Matches the file itself, a directory prefix, or
            // any segment along the way — "exclude vendor" must not depend on where vendor sits.
            if (rel.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                rel.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase) ||
                rel.Contains("/" + p + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // These patterns come from a cloned repository and are evaluated once per candidate file, in
    // the indexing loop. Two consequences the permission overlay already learned the hard way:
    // compile each pattern once (not per file), and give it a match timeout — `**a**a**a**a` is a
    // legal glob that translates to a backtracking regex, and a background pass must not be able
    // to freeze on it. A pattern that times out is treated as *not* matching: failing to exclude
    // indexes a file the project would rather skip, while failing open the other way would let a
    // repository hide files behind an expensive pattern.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex?> _globCache = new();
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    private static bool GlobMatches(string glob, string relPath)
    {
        var regex = _globCache.GetOrAdd(glob, g =>
        {
            try { return new Regex(RulesService.GlobToRegex(g), RegexOptions.IgnoreCase, MatchTimeout); }
            catch (ArgumentException ex) { Diagnostics.Swallow("IndexExclusions.Compile", ex); return null; }
        });
        if (regex is null) return false;

        try
        {
            if (regex.IsMatch(relPath)) return true;
            // Bare patterns (no separator) also match the file name at any depth — same rule as
            // the project rules' globs, so `*.generated.cs` works without writing `**/`.
            if (!glob.Contains('/'))
                return regex.IsMatch(relPath[(relPath.LastIndexOf('/') + 1)..]);
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            Diagnostics.Record("IndexExclusions",
                $"Exclusion pattern '{glob}' from .inferpal/project.json timed out; the file was indexed.");
            return false;
        }
    }

    private static string Relative(string path, string? root)
    {
        if (string.IsNullOrEmpty(root)) return path.Replace('\\', '/');
        try
        {
            var rel = Path.GetRelativePath(root!, path).Replace('\\', '/');
            // Outside the root: GetRelativePath answers with "../…", which no pattern should match.
            return rel.StartsWith("../", StringComparison.Ordinal) ? string.Empty : rel;
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("IndexExclusions.Relative", ex);
            return string.Empty;
        }
    }
}
