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

        // A blank old_content anchors nothing — the tolerant pass matched the file's first empty line
        // and replaced it.
        if (string.IsNullOrWhiteSpace(oldContent)) return new Result(null, 0, false);

        // ⚠ The replacement is written in the file's own line endings: a CRLF file (Visual Studio's
        // default) received the model's LF text as is — mixed endings on disk.
        var eol = LineEndings.Dominant(file);
        newContent = LineEndings.ToEol(newContent, eol);

        var exact = CountOccurrences(file, oldContent);
        // The same text in the file's endings: an LF old_content spanning lines still matches a CRLF
        // file exactly, instead of falling to the tolerant pass.
        if (exact == 0 && eol == "\r\n")
        {
            var inFileEndings = LineEndings.ToEol(oldContent, eol);
            if (inFileEndings != oldContent && (exact = CountOccurrences(file, inFileEndings)) > 0)
                oldContent = inFileEndings;
        }

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
    // leading/trailing whitespace and \r). Applies only when exactly one contiguous block matches;
    // several matches come back as ambiguous (Count > 1) — reporting them as "not found" sent the
    // model looking for a whitespace mistake that did not exist.
    private static Result? TryFuzzy(string file, string oldContent, string newContent)
    {
        var fileLines = file.Split('\n');
        var target    = oldContent.Replace("\r", "").Split('\n');
        // A trailing newline ends old_content's last line; it is not an extra empty line to match
        // (which made an old_content ending in "\n" require a blank line after the block).
        var endsWithNewline = target.Length > 1 && target[^1].Length == 0;
        if (endsWithNewline) target = target[..^1];
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
            if (matches++ == 0) matchStart = s;
        }
        if (matches > 1) return new Result(null, matches, true);   // ambiguous → too risky to fuzzy-apply
        if (matches == 0) return null;

        // The line structure already provides the break after the block: drop the one new_content ends with.
        if (endsWithNewline)
        {
            if (newContent.EndsWith("\r\n", StringComparison.Ordinal)) newContent = newContent[..^2];
            else if (newContent.EndsWith('\n'))                        newContent = newContent[..^1];
        }
        // Split on '\n', a CRLF line keeps its "\r": the last replaced line must end the same way.
        if (fileLines[matchStart + k - 1].EndsWith('\r') && !newContent.EndsWith('\r'))
            newContent += "\r";

        // Replace the matched lines in place: joining the untouched lines back with "\n" restores the
        // file exactly, including a final newline (an empty last element).
        var modified = string.Join("\n",
            fileLines[..matchStart].Append(newContent).Concat(fileLines[(matchStart + k)..]));

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
