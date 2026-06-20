namespace Inferpal.Services.Tools;

/// <summary>
/// Resolves an <c>apply_diff</c> edit against a file's content. Pure/testable (no IO/approval).
/// Tries an exact match first, honouring the <c>occurrence</c> mode, then falls back to a
/// whitespace-tolerant line match so a small model that reproduces <c>old_content</c> with slightly
/// different indentation / trailing spaces / line endings still lands the edit.
/// </summary>
internal static class ApplyDiffMatcher
{
    /// <summary>
    /// Outcome of resolving an edit. <see cref="Modified"/> is <c>null</c> on failure; inspect
    /// <see cref="Count"/> to tell "not found" (0) from "ambiguous" (&gt;1).
    /// </summary>
    internal sealed record Result(string? Modified, int Count, bool Fuzzy);

    /// <param name="occurrence"><c>"unique"</c> (default — require exactly one match),
    /// <c>"first"</c> (replace the first of several), or <c>"all"</c> (replace every match).</param>
    public static Result Resolve(string file, string oldContent, string newContent, string? occurrence)
    {
        var mode = (occurrence ?? "unique").Trim().ToLowerInvariant();

        var exact = CountOccurrences(file, oldContent);
        if (exact > 0)
        {
            switch (mode)
            {
                case "all":
                    return new Result(file.Replace(oldContent, newContent, StringComparison.Ordinal), exact, false);
                case "first":
                    return new Result(ReplaceFirst(file, oldContent, newContent), 1, false);
                default:
                    return exact == 1
                        ? new Result(file.Replace(oldContent, newContent, StringComparison.Ordinal), 1, false)
                        : new Result(null, exact, false);   // ambiguous → caller reports
            }
        }

        // ── Fuzzy fallback: unique whitespace-tolerant line block ──────────────
        return TryFuzzy(file, oldContent, newContent) ?? new Result(null, 0, false);
    }

    // Matches old_content against the file line-by-line, comparing each line trimmed (handles
    // leading/trailing whitespace and \r). Applies only when exactly one contiguous block matches.
    private static Result? TryFuzzy(string file, string oldContent, string newContent)
    {
        var fileLines = file.Split('\n');
        var target    = oldContent.Replace("\r", "").Split('\n');
        int k = target.Length;
        if (k == 0 || k > fileLines.Length) return null;

        var targetTrim = target.Select(l => l.Trim()).ToArray();

        int matchStart = -1, matches = 0;
        for (int s = 0; s + k <= fileLines.Length; s++)
        {
            var ok = true;
            for (int j = 0; j < k; j++)
                if (!fileLines[s + j].Trim().Equals(targetTrim[j], StringComparison.Ordinal)) { ok = false; break; }
            if (!ok) continue;
            if (++matches > 1) return null;   // not unique → too risky to fuzzy-apply
            matchStart = s;
        }
        if (matches != 1) return null;

        var before = string.Join("\n", fileLines[..matchStart]);
        var after  = string.Join("\n", fileLines[(matchStart + k)..]);
        var modified =
            (before.Length > 0 ? before + "\n" : string.Empty) +
            newContent +
            (after.Length > 0 ? "\n" + after : string.Empty);

        return new Result(modified, 1, true);
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var idx = text.IndexOf(oldValue, StringComparison.Ordinal);
        return idx < 0 ? text : text[..idx] + newValue + text[(idx + oldValue.Length)..];
    }

    private static int CountOccurrences(string text, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return 0;
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }
}
