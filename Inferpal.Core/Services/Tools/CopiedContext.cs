namespace Inferpal.Services.Tools;

/// <summary>
/// Whether an edit's replacement text replays lines that stay in the file around the text it replaces: an
/// <c>old_content</c> that is a fragment and a <c>new_content</c> that is the whole page leave the page's head twice.
/// </summary>
/// <remarks>
/// Three forms, each a copy only of lines the replacement ADDS (it has more lines than the zone by at least as many):
/// its first lines are the file's first lines or the lines just before the zone (<see cref="Side.Head"/>); its last
/// lines are the file's last lines or the lines just after (<see cref="Side.Tail"/>); or it holds most of what stays
/// around the zone (<see cref="Side.Around"/>), wherever. ⚠ The copy is not always exact: a model that copies the page
/// also retouches it, which only the third form sees whole. ⚠ Lines are compared trimmed, blank lines skipped: the
/// matcher tolerates indentation, and a reindented copy is still a copy. ⚠ What the zone itself begins (or ends) with
/// is never a copy of the context — the model rewrites its own text, as repetitive code does —, and only lines with a
/// word in them count: two closing braces at a seam are code, not a copy.
/// </remarks>
internal static class CopiedContext
{
    /// <summary>Where the replacement replays what stays: at its head, at its tail, or most of it around.</summary>
    internal enum Side { Head, Tail, Around }

    /// <summary>The copy: where, how many lines, and the first of them as the replacement writes it.</summary>
    internal sealed record Copy(Side Side, int Lines, string First);

    /// <summary>What every refusal of a copy says first, in whatever a tool wraps it.</summary>
    internal const string RefusalMarker = "Refused: this edit repeats lines that stay in ";

    /// <summary>What a refusal of a copy that offers the pass says: only within a run.</summary>
    internal const string PassOffered =
        " Only if the repeated lines are meant to be there twice, send the same edit again and it will be written.";

    /// <summary>What the refusal of <paramref name="copy"/> in <paramref name="shown"/> (the file, as the model knows it)
    /// tells the model: what was seen, that nothing was written, and what to send instead.</summary>
    internal static string Refusal(Copy copy, string shown)
    {
        // Never cut between the two halves of a surrogate pair: a lone one is not valid text in the request.
        var cut   = copy.First.Length > 80 && char.IsHighSurrogate(copy.First[79]) ? 79 : 80;
        var first = copy.First.Length > 80 ? copy.First[..cut] + "…" : copy.First;
        var what = copy.Side switch
        {
            Side.Head => $"starts with {copy.Lines} lines that stay in the file before the text old_content matched",
            Side.Tail => $"ends with {copy.Lines} lines that stay in the file after the text old_content matched",
            _         => $"repeats {copy.Lines} of the lines that stay in the file around the text old_content matched",
        };
        return $"{RefusalMarker}{shown}: new_content {what} (from \"{first}\"), so the file would hold them twice. "
             + "Nothing was written: either put in old_content everything new_content rewrites, or send in new_content "
             + "only what replaces old_content (to rewrite the whole file, use write_file).";
    }

    // shortcut: a copy at a seam longer than this many lines is not looked for (a copy of the file's first or last
    // lines is, whatever its length), nor a copy beyond the first zones of occurrence "all"; raise them if one is seen.
    private const int MaxSeamLines = 512, MaxZones = 64;

    /// <summary>Of the lines with a word that stay around the zone, the share the replacement must add for
    /// <see cref="Side.Around"/>: a whole page, retouched.</summary>
    private const double AroundShare = 0.8;

    /// <summary>The first copy found at any of <paramref name="zones"/> (the ranges of <paramref name="file"/> that
    /// <paramref name="replacement"/> replaces), or <c>null</c>.</summary>
    /// <remarks>⚠ Each zone is judged against the file as it was: with occurrence "all", what stays before the second
    /// zone holds the first one's original text.</remarks>
    public static Copy? Find(string file, IReadOnlyList<Range> zones, string replacement)
    {
        var r = NonBlank(replacement);
        if (zones.Count == 0 || r.Length < 2) return null;

        var lines   = file.Split('\n');
        var offsets = LineStarts(lines);
        // The file's non-blank lines, kept, and the line each one is on: what lies before or after a zone is a slice.
        var at   = new List<int>();
        var kept = new List<string>();
        for (int i = 0; i < lines.Length; i++)
            if (Kept(lines[i]) is { Length: > 0 } line) { at.Add(i); kept.Add(line); }
        var rBack = Enumerable.Reverse(r).ToArray();

        foreach (var zone in zones.Take(MaxZones))
        {
            var (start, length) = zone.GetOffsetAndLength(file.Length);
            var end = start + length;
            // A line partly in the zone belongs to neither side: it is the line the replacement starts or ends on.
            // A zone that starts on a line's break leaves that line whole before it.
            var startLine     = LineOf(offsets, start);
            var beforeLimit   = start >= offsets[startLine] + lines[startLine].TrimEnd('\r').Length ? startLine + 1 : startLine;
            var endLine       = LineOf(offsets, end);
            var afterFrom     = end == offsets[endLine] ? endLine : endLine + 1;
            var beforeCount   = LowerBound(at, beforeLimit);     // kept[..beforeCount] is before the zone
            var afterStart    = LowerBound(at, afterFrom);       // kept[afterStart..] is after it
            var own           = NonBlank(file[start..end]);
            var added         = r.Length - own.Length;
            if (added < 2) continue;

            // The head against the file's first lines and the lines just before; the tail, the same question reversed.
            // Neither needs more lines than the replacement adds.
            var reach = Math.Min(r.Length, added);
            var seam  = Math.Min(beforeCount, Math.Min(reach, MaxSeamLines));
            var head  = Replayed(kept.GetRange(0, Math.Min(beforeCount, reach)).ToArray(),
                                 kept.GetRange(beforeCount - seam, seam).ToArray(), own, r);
            if (head > 0) return new Copy(Side.Head, head, r[0]);

            var afterCount = kept.Count - afterStart;
            var last       = Math.Min(afterCount, reach);
            seam           = Math.Min(afterCount, Math.Min(reach, MaxSeamLines));
            var tail = Replayed(kept.GetRange(kept.Count - last, last).AsEnumerable().Reverse().ToArray(),
                                kept.GetRange(afterStart, seam).AsEnumerable().Reverse().ToArray(),
                                Enumerable.Reverse(own).ToArray(), rBack);
            if (tail > 0) return new Copy(Side.Tail, tail, r[^tail]);

            if (Around(kept.Take(beforeCount).Concat(kept.Skip(afterStart)), own, r) is { } around) return around;
        }
        return null;
    }

    // How many of r's first lines replay the file's first lines (`first`, from the top) or the lines just before the
    // zone (`seam`, ending at it); 0 when none, or when r begins as the zone's own text does.
    private static int Replayed(string[] first, string[] seam, string[] own, string[] r)
    {
        if (Same(own, 0, r, 0, 2)) return 0;
        var fromStart = 0;
        while (fromStart < first.Length && fromStart < r.Length && first[fromStart] == r[fromStart]) fromStart++;
        if (fromStart >= 2 && HasWord(r, fromStart)) return fromStart;
        for (int k = 2; k <= seam.Length; k++)
            if (Same(seam, seam.Length - k, r, 0, k) && HasWord(r, k)) return k;
        return 0;
    }

    // The lines with a word that stay around the zone and that r adds (counted as many times as r adds them beyond
    // the zone's own), when they are at least three and most of what stays.
    private static Copy? Around(IEnumerable<string> context, string[] own, string[] r)
    {
        var stays = context.Where(Word).ToList();
        if (stays.Count < 3) return null;
        var adds = r.Where(Word).GroupBy(l => l).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var l in own) if (adds.TryGetValue(l, out var n)) adds[l] = n - 1;
        var replayed = 0;
        foreach (var l in stays)
            if (adds.TryGetValue(l, out var n) && n > 0) { replayed++; adds[l] = n - 1; }
        if (replayed < 3 || replayed < AroundShare * stays.Count) return null;
        // Quoted: a replayed line the zone does not hold, so the model is pointed at the copy, not at its own text.
        return new Copy(Side.Around, replayed,
                        r.FirstOrDefault(l => stays.Contains(l) && !own.Contains(l)) ?? r.First(stays.Contains));
    }

    private static bool Same(string[] a, int from, string[] b, int bFrom, int count)
    {
        if (a.Length - from < count || b.Length - bFrom < count) return false;
        for (int j = 0; j < count; j++)
            if (!string.Equals(a[from + j], b[bFrom + j], StringComparison.Ordinal)) return false;
        return true;
    }

    private static bool Word(string line) => line.Any(char.IsLetterOrDigit);

    private static bool HasWord(string[] r, int count) => r.Take(count).Any(Word);

    // A BOM is not whitespace to Trim, and a copy never carries it.
    private static string Kept(string line) => line.Trim().Trim('\uFEFF').Trim();

    private static string[] NonBlank(string text) => text.Split('\n').Select(Kept).Where(l => l.Length > 0).ToArray();

    /// <summary>Where each of <paramref name="lines"/> (a text split on <c>'\n'</c>) starts in the text: the one table
    /// the matcher's zones and this guard's slices are read from.</summary>
    internal static int[] LineStarts(string[] lines)
    {
        var offsets = new int[lines.Length];
        for (int i = 1; i < lines.Length; i++) offsets[i] = offsets[i - 1] + lines[i - 1].Length + 1;
        return offsets;
    }

    // The index of the line holding offset `at`.
    private static int LineOf(int[] offsets, int at)
    {
        var i = Array.BinarySearch(offsets, at);
        return i >= 0 ? i : ~i - 1;
    }

    // How many of the (sorted) values are below `value`.
    private static int LowerBound(List<int> sorted, int value)
    {
        var i = sorted.BinarySearch(value);
        return i >= 0 ? i : ~i;
    }
}
