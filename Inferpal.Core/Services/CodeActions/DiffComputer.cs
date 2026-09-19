using System.Text;
using Inferpal.Localization;

namespace Inferpal.Services.CodeActions;

internal static class DiffComputer
{
    // ⚠ This caps the REGION THAT DIFFERS, never the size of the file. Read off the whole file, a
    // single changed line in a 400-line source shows a sentence instead of the change, at the
    // **approval prompt** — the one surface where the human reads what they are agreeing to, on
    // exactly the large files an assistant edits most.
    //
    // What the cap protects is real: the LCS below is O(m·n) and allocates an int[m+1, n+1] —
    // 8,000 lines would be 256 MB, inside a modal dialog. Keeping out of the computation what is
    // identical on both sides is what makes both true at once: after trimming, a one-line change
    // costs a 7×7 DP. ⚠ And `MaxLines` (the region) is not ComputeText's `maxLines` parameter
    // (rendered lines) — two different things, one letter apart.
    private const int MaxWindowLines = 300;
    private const int CtxLines       = 3;

    /// <summary>
    /// Renders a compact textual diff (context-collapsed, prefixes <c>+ - …</c>) for the approval
    /// prompt, capped to <paramref name="maxLines"/> shown lines. Returns <c>null</c> when the two
    /// texts are identical (nothing to confirm visually).
    /// </summary>
    public static string? ComputeText(string oldText, string newText, int maxLines = 30)
    {
        var lines = Compute(oldText, newText);
        if (lines.Count == 0) return null;

        var sb = new StringBuilder();
        var shown = 0;
        foreach (var l in lines)
        {
            if (shown >= maxLines)
            {
                sb.Append(Strings.DiffMoreLines(lines.Count - shown));
                break;
            }
            sb.Append(l.Prefix).Append(l.Text).Append('\n');
            shown++;
        }
        return sb.ToString().TrimEnd('\n');
    }

    public static List<DiffLineModel> Compute(string oldText, string newText)
    {
        if (oldText == newText)
            return [];

        var old  = oldText.Split('\n');
        var @new = newText.Split('\n');

        // An identical head and tail cannot belong to any change, so they never enter the
        // computation. CtxLines of each stay INSIDE the window, so the change still reads with its
        // usual context; the rest becomes a collapsed run — the same shape as an interior gap.
        var head = 0;
        while (head < old.Length && head < @new.Length && old[head] == @new[head])
            head++;

        var tail = 0;
        while (tail < old.Length - head && tail < @new.Length - head
               && old[old.Length - 1 - tail] == @new[@new.Length - 1 - tail])
            tail++;

        var ctxHead = Math.Min(head, CtxLines);
        var ctxTail = Math.Min(tail, CtxLines);

        var oldWin = old[(head - ctxHead)..(old.Length - tail + ctxTail)];
        var newWin = @new[(head - ctxHead)..(@new.Length - tail + ctxTail)];

        // The cap only bites on what genuinely differs (a whole-file rewrite, a binary read as
        // text) — and it says so in all ten languages: this sentence reaches the approval prompt.
        if (oldWin.Length > MaxWindowLines || newWin.Length > MaxWindowLines)
            return
            [
                new DiffLineModel { Prefix = "…", Text = Strings.DiffTooLarge(old.Length, @new.Length) }
            ];

        var m  = oldWin.Length;
        var n  = newWin.Length;
        var dp = new int[m + 1, n + 1];
        for (var i = 1; i <= m; i++)
            for (var j = 1; j <= n; j++)
                dp[i, j] = oldWin[i - 1] == newWin[j - 1]
                    ? dp[i - 1, j - 1] + 1
                    : Math.Max(dp[i - 1, j], dp[i, j - 1]);

        var lines = CollapseContext(Backtrack(dp, oldWin, newWin));

        // What was trimmed before the computation is still owed to the reader: without these two
        // lines they believe the file starts — and ends — where the window starts and ends.
        if (head - ctxHead > 0)
            lines.Insert(0, new DiffLineModel { Prefix = "…", Text = Strings.DiffUnchangedLines(head - ctxHead) });
        if (tail - ctxTail > 0)
            lines.Add(new DiffLineModel { Prefix = "…", Text = Strings.DiffUnchangedLines(tail - ctxTail) });

        return lines;
    }

    private static List<DiffLineModel> Backtrack(int[,] dp, string[] old, string[] @new)
    {
        var path = new List<DiffLineModel>();
        var i    = old.Length;
        var j    = @new.Length;

        while (i > 0 || j > 0)
        {
            if (i > 0 && j > 0 && old[i - 1] == @new[j - 1])
            {
                path.Add(new DiffLineModel { Prefix = " ", Text = old[i - 1] });
                i--; j--;
            }
            else if (j > 0 && (i == 0 || dp[i, j - 1] >= dp[i - 1, j]))
            {
                path.Add(new DiffLineModel { Prefix = "+", Text = @new[j - 1] });
                j--;
            }
            else
            {
                path.Add(new DiffLineModel { Prefix = "-", Text = old[i - 1] });
                i--;
            }
        }

        path.Reverse();
        return path;
    }

    private static List<DiffLineModel> CollapseContext(List<DiffLineModel> lines)
    {
        var keep = new bool[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Prefix == " ") continue;
            for (var k = Math.Max(0, i - CtxLines); k <= Math.Min(lines.Count - 1, i + CtxLines); k++)
                keep[k] = true;
        }

        if (keep.All(k => !k))
            return lines;

        var result  = new List<DiffLineModel>();
        var skipped = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (keep[i])
            {
                if (skipped > 0)
                {
                    result.Add(new DiffLineModel { Prefix = "…", Text = Strings.DiffUnchangedLines(skipped) });
                    skipped = 0;
                }
                result.Add(lines[i]);
            }
            else
            {
                skipped++;
            }
        }
        if (skipped > 0)
            result.Add(new DiffLineModel { Prefix = "…", Text = Strings.DiffUnchangedLines(skipped) });

        return result;
    }
}
