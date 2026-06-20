using System.Text;
using Inferpal.ToolWindow;

namespace Inferpal.Services;

internal static class DiffComputer
{
    private const int MaxLines  = 300;
    private const int CtxLines  = 3;

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
                sb.Append("… (+").Append(lines.Count - shown).Append(" more diff line(s))");
                break;
            }
            sb.Append(l.Prefix).Append(l.Text).Append('\n');
            shown++;
        }
        return sb.ToString().TrimEnd('\n');
    }

    public static List<DiffLine> Compute(string oldText, string newText)
    {
        if (oldText == newText)
            return [];

        var old  = oldText.Split('\n');
        var @new = newText.Split('\n');

        if (old.Length > MaxLines || @new.Length > MaxLines)
            return
            [
                new DiffLine { Prefix = "…", Text = $"Fichier trop grand pour afficher le diff ({old.Length} → {@new.Length} lignes)", Background = "Transparent", Foreground = "#555555" }
            ];

        var m  = old.Length;
        var n  = @new.Length;
        var dp = new int[m + 1, n + 1];
        for (var i = 1; i <= m; i++)
            for (var j = 1; j <= n; j++)
                dp[i, j] = old[i - 1] == @new[j - 1]
                    ? dp[i - 1, j - 1] + 1
                    : Math.Max(dp[i - 1, j], dp[i, j - 1]);

        return CollapseContext(Backtrack(dp, old, @new));
    }

    private static List<DiffLine> Backtrack(int[,] dp, string[] old, string[] @new)
    {
        var path = new List<DiffLine>();
        var i    = old.Length;
        var j    = @new.Length;

        while (i > 0 || j > 0)
        {
            if (i > 0 && j > 0 && old[i - 1] == @new[j - 1])
            {
                path.Add(new DiffLine { Prefix = " ", Text = old[i - 1], Background = "Transparent",  Foreground = "#808080" });
                i--; j--;
            }
            else if (j > 0 && (i == 0 || dp[i, j - 1] >= dp[i - 1, j]))
            {
                path.Add(new DiffLine { Prefix = "+", Text = @new[j - 1], Background = "#1A3A1A", Foreground = "#6DB96D" });
                j--;
            }
            else
            {
                path.Add(new DiffLine { Prefix = "-", Text = old[i - 1], Background = "#3A1A1A",  Foreground = "#F47C7C" });
                i--;
            }
        }

        path.Reverse();
        return path;
    }

    private static List<DiffLine> CollapseContext(List<DiffLine> lines)
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

        var result  = new List<DiffLine>();
        var skipped = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (keep[i])
            {
                if (skipped > 0)
                {
                    result.Add(new DiffLine { Prefix = "…", Text = $"  {skipped} unchanged line(s)", Background = "Transparent", Foreground = "#444444" });
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
            result.Add(new DiffLine { Prefix = "…", Text = $"  {skipped} unchanged line(s)", Background = "Transparent", Foreground = "#444444" });

        return result;
    }
}
