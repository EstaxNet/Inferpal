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

    /// <summary>Above this many line comparisons, <see cref="Closest"/> says nothing rather than stall the reply.</summary>
    private const long MaxClosestComparisons = 2_000_000;

    /// <summary>A line no closer than this to its counterpart is a different line, not a near copy.</summary>
    private const double CloseLineRatio = 0.6;

    /// <summary>
    /// For an <c>old_content</c> that matched nothing: the file's closest block, and its first line that differs from
    /// <c>old_content</c>, copied from the file — or <c>null</c> when no block of the file is close.
    /// </summary>
    /// <remarks>
    /// ⚠ The tolerant pass already ignores indentation, trailing spaces and line endings, so what is left between a
    /// near copy and the file is a character inside a line — a dropped <c>$</c> before an interpolated string, a
    /// changed quote. A model that re-reads the file sees the line it believes it copied and sends the same call
    /// again, until the loop detector stops the run. The line from the file, and where it differs, is what it can act
    /// on. Addressed to the model, so in English.
    /// </remarks>
    internal static string? Closest(string file, string oldContent)
    {
        var fileLines = file.Replace("\r", "").Split('\n');
        var trimmed   = fileLines.Select(l => l.Trim()).ToArray();
        var target    = oldContent.Replace("\r", "").Split('\n').Select(l => l.Trim()).ToList();
        while (target.Count > 0 && target[^1].Length == 0) target.RemoveAt(target.Count - 1);
        while (target.Count > 0 && target[0].Length == 0)  target.RemoveAt(0);
        int k = target.Count;
        if (k == 0 || k > trimmed.Length || (long)k * (trimmed.Length - k + 1) > MaxClosestComparisons) return null;

        var (bestStart, bestScore) = (-1, 0.0);
        for (int s = 0; s + k <= trimmed.Length; s++)
        {
            var score = 0.0;
            for (int j = 0; j < k; j++) score += Similarity(trimmed[s + j], target[j]);
            if (score > bestScore) (bestStart, bestScore) = (s, score);
        }
        if (bestStart < 0 || bestScore < k * CloseLineRatio) return null;

        var differing = Enumerable.Range(0, k)
            .Where(j => !trimmed[bestStart + j].Equals(target[j], StringComparison.Ordinal)).ToList();
        if (differing.Count == 0) return null;
        var first    = differing[0];
        var inFile   = trimmed[bestStart + first];
        var inOld    = target[first];
        var lineNo   = bestStart + first + 1;
        var common   = CommonPrefix(inFile, inOld);
        var after    = common == 0 ? "at the start of the line"
                     : $"after \"{(common > 30 ? "…" + inFile[(common - 30)..common] : inFile[..common])}\"";
        var range    = k == 1 ? $"line {lineNo}" : $"lines {bestStart + 1}–{bestStart + k}";
        var others   = differing.Count > 1 ? $" {differing.Count - 1} other line(s) of that block differ too." : string.Empty;
        return $"\nClosest text in the file: {range}. Line {lineNo} differs from old_content, first {after}:\n"
             + $"  file:        {Shown(inFile, common)}\n"
             + $"  old_content: {Shown(inOld, common)}\n"
             + $"Copy the line from the file exactly (indentation and trailing spaces do not matter).{others}";
    }

    // How close two trimmed lines are: what they share at both ends, over the longer one (1 = identical).
    private static double Similarity(string a, string b)
    {
        if (a.Equals(b, StringComparison.Ordinal)) return 1;
        var longer = Math.Max(a.Length, b.Length);
        if (longer == 0) return 1;
        var prefix = CommonPrefix(a, b);
        var suffix = 0;
        while (suffix < Math.Min(a.Length, b.Length) - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;
        var ratio = (double)(prefix + suffix) / longer;
        return ratio >= CloseLineRatio ? ratio : 0;
    }

    private static int CommonPrefix(string a, string b)
    {
        var n = 0;
        while (n < a.Length && n < b.Length && a[n] == b[n]) n++;
        return n;
    }

    // A long line is shown around its first difference, and says so: a model copies what it is shown.
    private static string Shown(string line, int at)
    {
        if (line.Length == 0) return "(empty line)";
        if (line.Length <= 240) return line;
        var from = Math.Max(0, at - 100);
        var to   = Math.Min(line.Length, at + 100);
        return $"{(from > 0 ? "…" : "")}{line[from..to]}{(to < line.Length ? "…" : "")} (part of a {line.Length}-character line)";
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
