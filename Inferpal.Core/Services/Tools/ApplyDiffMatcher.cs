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

    /// <summary>The whole vocabulary of <c>occurrence</c>, written once.</summary>
    private static readonly string[] Modes = ["unique", "first", "all"];

    /// <summary>
    /// The message for an <c>occurrence</c> outside <see cref="Modes"/>, or <c>null</c> when it is
    /// one of them (or absent, which means the default).
    /// </summary>
    /// <remarks>
    /// ⚠ Ten tools read a keyword the model wrote; eight name a value they do not recognise, and the
    /// two that did not are the two that <b>write</b>. Here an unknown value fell into
    /// <see cref="Resolve"/>'s <c>default:</c> — that is, <c>"unique"</c> — so
    /// <c>occurrence: "every"</c> on three matches came back as <i>"ambiguous (3 matches)"</i>: the
    /// model is told its <c>old_content</c> is the problem, rewrites a perfectly good one, and
    /// applies one edit where it wanted three. One reader for both tools, because the vocabulary
    /// copied on each side is the drift this repository pays for elsewhere.
    /// </remarks>
    public static string? RejectOccurrence(string? occurrence) =>
        occurrence is null || Modes.Contains(occurrence.Trim().ToLowerInvariant())
            ? null
            : $"Unknown occurrence '{occurrence.Trim()}'. Use one of: 'unique' (default: require "
            + "exactly one match), 'first' (replace the first of several), or 'all' (replace every match).";

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
        return TryFuzzy(file, oldContent, newContent, mode) ?? new Result(null, 0, false);
    }

    // Matches old_content against the file line-by-line, comparing each line trimmed (handles
    // leading/trailing whitespace and \r), honouring the occurrence mode like the exact pass. Under
    // "unique", several blocks come back as ambiguous (Count > 1) — reporting them as "not found" sent
    // the model looking for a whitespace mistake that did not exist.
    // ⚠ The mode reaches this pass too: ignored, "first" and "all" on two blocks answered "found 2 times — make the
    // match unique", the one remedy the model had just declined by asking for every match.
    private static Result? TryFuzzy(string file, string oldContent, string newContent, string mode)
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

        var starts = new List<int>();
        for (int s = 0; s + k <= fileLines.Length; s++)
        {
            var ok = true;
            for (int j = 0; j < k; j++)
                if (!fileLines[s + j].Trim().Equals(targetTrim[j], StringComparison.Ordinal)) { ok = false; break; }
            if (ok) starts.Add(s);
        }
        if (starts.Count == 0) return null;
        if (starts.Count > 1 && mode is not ("first" or "all"))
            return new Result(null, starts.Count, true);   // ambiguous → too risky to fuzzy-apply

        // The blocks replaced: the first, or every one that does not overlap the previous — as the exact pass does.
        var chosen = new List<int>();
        foreach (var s in starts)
        {
            if (chosen.Count > 0 && (mode != "all" || s < chosen[^1] + k)) continue;
            chosen.Add(s);
        }

        // The line structure already provides the break after the block: drop the one new_content ends with.
        if (endsWithNewline)
        {
            if (newContent.EndsWith("\r\n", StringComparison.Ordinal)) newContent = newContent[..^2];
            else if (newContent.EndsWith('\n'))                        newContent = newContent[..^1];
        }
        // Replace the matched lines in place: joining the untouched lines back with "\n" restores the
        // file exactly, including a final newline (an empty last element).
        var lines = new List<string>(fileLines.Length);
        var at    = 0;
        foreach (var start in chosen)
        {
            lines.AddRange(fileLines[at..start]);
            // Split on '\n', a CRLF line keeps its "\r": the last replaced line must end the same way.
            lines.Add(fileLines[start + k - 1].EndsWith('\r') && !newContent.EndsWith('\r') ? newContent + "\r" : newContent);
            at = start + k;
        }
        lines.AddRange(fileLines[at..]);

        return new Result(string.Join("\n", lines), chosen.Count, true);
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
