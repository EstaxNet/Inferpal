namespace Inferpal.Services.CodeActions;

/// <summary>One contiguous change of a <see cref="DiffPlan"/>: <see cref="OldLines"/> (at 0-based
/// <see cref="OldStart"/>) are replaced by <see cref="NewLines"/>. Insertions have an empty
/// <see cref="OldLines"/>, deletions an empty <see cref="NewLines"/>.</summary>
internal sealed record DiffHunk(
    int Index,
    int OldStart,
    int NewStart,
    IReadOnlyList<string> OldLines,
    IReadOnlyList<string> NewLines);

/// <summary>Positional line-diff between two texts, split into independently acceptable hunks
/// (the model behind the inline diff preview — editor front-ends render it, the Core applies it).</summary>
internal sealed record DiffPlan(string OldText, string NewText, IReadOnlyList<DiffHunk> Hunks)
{
    public bool HasChanges => Hunks.Count > 0;
}

/// <summary>
/// Builds a <see cref="DiffPlan"/> (LCS line diff, contiguous changes grouped into hunks) and
/// applies a per-hunk accept/reject decision to produce the merged text. Pure and deterministic:
/// accepting every hunk reproduces the new text exactly, rejecting every hunk the old text —
/// guaranteed by construction and locked by tests. Lines keep their own <c>\r</c> so CRLF files
/// round-trip byte-identically.
/// </summary>
internal static class InlineDiffPlanner
{
    // O(m·n) LCS table — beyond this the plan degrades to a single all-or-nothing hunk
    // (selection-based inline edits are small; whole-file edits go through apply_diff instead).
    private const int MaxLines = 2000;

    public static DiffPlan Plan(string oldText, string newText)
    {
        if (oldText == newText)
            return new DiffPlan(oldText, newText, []);

        var old  = oldText.Split('\n');
        var @new = newText.Split('\n');

        if (old.Length > MaxLines || @new.Length > MaxLines)
            return new DiffPlan(oldText, newText,
                [new DiffHunk(1, 0, 0, old, @new)]);   // fallback: one whole-text hunk

        // LCS table, then walk both texts forward grouping contiguous non-matches into hunks.
        var dp = LcsTable(old, @new);
        var hunks = new List<DiffHunk>();
        int i = 0, j = 0;
        while (i < old.Length || j < @new.Length)
        {
            if (i < old.Length && j < @new.Length && old[i] == @new[j])
            {
                i++; j++;
                continue;
            }

            var oldStart = i;
            var newStart = j;
            var removed  = new List<string>();
            var added    = new List<string>();
            while (i < old.Length || j < @new.Length)
            {
                if (i < old.Length && j < @new.Length && old[i] == @new[j]) break;
                // Same tie-break as the backward walk in DiffComputer, mirrored forward:
                // prefer consuming the new side when the LCS length is unaffected.
                if (j < @new.Length && (i == old.Length || dp[i, j + 1] >= dp[i + 1, j]))
                    added.Add(@new[j++]);
                else
                    removed.Add(old[i++]);
            }
            hunks.Add(new DiffHunk(hunks.Count + 1, oldStart, newStart, removed, added));
        }

        return new DiffPlan(oldText, newText, hunks);
    }

    /// <summary>
    /// Merged text after applying only the hunks whose <see cref="DiffHunk.Index"/> is in
    /// <paramref name="acceptedHunks"/>; rejected hunks keep the original lines.
    /// </summary>
    public static string Apply(DiffPlan plan, IReadOnlyCollection<int> acceptedHunks)
    {
        if (!plan.HasChanges) return plan.OldText;

        var old    = plan.OldText.Split('\n');
        var result = new List<string>(old.Length);
        var cursor = 0;

        foreach (var hunk in plan.Hunks)
        {
            for (; cursor < hunk.OldStart; cursor++)
                result.Add(old[cursor]);

            if (acceptedHunks.Contains(hunk.Index))
            {
                result.AddRange(hunk.NewLines);
                cursor += hunk.OldLines.Count;
            }
            // rejected: the old lines flow through the loop below / next iterations
        }
        for (; cursor < old.Length; cursor++)
            result.Add(old[cursor]);

        return string.Join('\n', result);
    }

    private static int[,] LcsTable(string[] old, string[] @new)
    {
        // Suffix-LCS (dp[i, j] = LCS of old[i..], new[j..]) so the forward walk can use it directly.
        var m  = old.Length;
        var n  = @new.Length;
        var dp = new int[m + 1, n + 1];
        for (var i = m - 1; i >= 0; i--)
            for (var j = n - 1; j >= 0; j--)
                dp[i, j] = old[i] == @new[j]
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
        return dp;
    }
}
