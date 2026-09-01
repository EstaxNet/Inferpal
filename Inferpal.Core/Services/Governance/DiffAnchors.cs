using System.Text.RegularExpressions;

namespace Inferpal.Services.Governance;

/// <summary>
/// The set of lines a unified diff actually touches, per file — the only positions a review
/// finding may legitimately point at.
/// </summary>
/// <remarks>
/// <para>
/// A model asked for <c>file:line</c> will happily produce a plausible one. Without something to
/// check it against, a wrong line is indistinguishable from a right one, and the whole promise of
/// an anchored review ("click the finding, land on the code") turns into clicking into the void.
/// This class is that check: it is built from the exact diff text sent to the model, so a finding
/// either falls inside a changed hunk or it does not.
/// </para>
/// <para>Pure and side-effect free: it parses text and answers questions about it.</para>
/// </remarks>
internal sealed class DiffAnchors
{
    // "@@ -12,3 +40,7 @@" — only the new-side range matters: findings point at the code as it is
    // after the change, which is what the reviewer opens in the editor.
    private static readonly Regex HunkHeader = new(
        @"^@@ -\d+(?:,\d+)? \+(?<start>\d+)(?:,(?<count>\d+))? @@", RegexOptions.Compiled, RegexBudget.Default);

    // "+++ b/path/to/file.cs" — the new path. "/dev/null" means the file was deleted.
    private static readonly Regex NewFile = new(
        @"^\+\+\+ (?:b/)?(?<path>.+)$", RegexOptions.Compiled, RegexBudget.Default);

    private readonly Dictionary<string, List<(int From, int To)>> _byFile =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the changed line ranges out of a unified diff. Tolerates the surrounding text our own
    /// context builders add (<c>git status:</c> headers, a truncation marker): anything that is not
    /// a file header or a hunk header is skipped.
    /// </summary>
    public static DiffAnchors Parse(string? unifiedDiff)
    {
        var anchors = new DiffAnchors();
        if (string.IsNullOrWhiteSpace(unifiedDiff)) return anchors;

        string? file = null;
        foreach (var raw in unifiedDiff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (NewFile.Match(line) is { Success: true } nf)
            {
                var path = nf.Groups["path"].Value.Trim();
                // A trailing tab-separated timestamp is legal in unified diffs.
                int tab = path.IndexOf('\t');
                if (tab >= 0) path = path[..tab];
                file = path == "/dev/null" ? null : Normalize(path);
                continue;
            }

            if (file is null) continue;
            if (HunkHeader.Match(line) is not { Success: true } hh) continue;

            int start = int.Parse(hh.Groups["start"].Value);
            int count = hh.Groups["count"].Success ? int.Parse(hh.Groups["count"].Value) : 1;
            // "+40,0" marks a pure deletion: there is no new line to point at, but the position
            // is still meaningful, so keep it as a single-line anchor.
            if (count == 0) count = 1;

            if (!anchors._byFile.TryGetValue(file, out var ranges))
                anchors._byFile[file] = ranges = [];
            ranges.Add((start, start + count - 1));
        }
        return anchors;
    }

    /// <summary>Whether <paramref name="line"/> of <paramref name="file"/> is inside a changed hunk.</summary>
    public bool Covers(string file, int line) =>
        Ranges(file) is { } ranges && ranges.Any(r => line >= r.From && line <= r.To);

    /// <summary>
    /// The closest changed line in <paramref name="file"/>, or null when the diff does not touch
    /// that file at all. Used to re-anchor a finding whose line is off by a few — never to
    /// re-anchor across files, which would be inventing a location rather than correcting one.
    /// </summary>
    public int? Nearest(string file, int line)
    {
        if (Ranges(file) is not { } ranges || ranges.Count == 0) return null;

        int best = ranges[0].From, bestDistance = int.MaxValue;
        foreach (var (from, to) in ranges)
        {
            int candidate = line < from ? from : line > to ? to : line;
            int distance  = Math.Abs(candidate - line);
            if (distance < bestDistance) { bestDistance = distance; best = candidate; }
        }
        return best;
    }

    /// <summary>Whether the diff touches this file, whatever the line.</summary>
    public bool HasFile(string file) => Ranges(file) is not null;

    private List<(int From, int To)>? Ranges(string file)
    {
        var key = Normalize(file);
        if (_byFile.TryGetValue(key, out var exact)) return exact;

        // The model often shortens a path to its file name, or quotes it relative to a sub-folder.
        // Accept a suffix match, but only when it is unambiguous: two files with the same name in
        // different folders must not silently resolve to whichever came first.
        List<(int, int)>? single = null;
        foreach (var (path, ranges) in _byFile)
        {
            if (!path.EndsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
            // Guard against "Tool.cs" matching "MyTool.cs".
            if (path.Length > key.Length && path[path.Length - key.Length - 1] is not '/') continue;
            if (single is not null) return null;
            single = ranges;
        }
        return single;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim();
}
