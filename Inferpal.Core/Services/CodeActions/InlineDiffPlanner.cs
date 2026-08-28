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

/// <summary>One hunk of a <see cref="DiffPlan"/> as a character-offset edit against
/// <see cref="DiffPlan.OldText"/>: replace [<see cref="Start"/>, <see cref="End"/>) with
/// <see cref="NewText"/>. Edits never overlap and are ordered by position, so a front-end
/// can hand any accepted subset to its native edit API (e.g. a VS Code WorkspaceEdit)
/// without offset arithmetic of its own.</summary>
internal sealed record DiffEdit(int Index, int Start, int End, string NewText);

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

        return string.Join("\n",result);
    }

    /// <summary>
    /// Converts every hunk into a character-offset <see cref="DiffEdit"/> against
    /// <see cref="DiffPlan.OldText"/>. Applying every edit reproduces the new text exactly,
    /// applying a subset matches <see cref="Apply"/> on that subset — locked by tests.
    /// The delicate cases live here (last line without trailing newline, deletions and
    /// insertions touching EOF) so front-ends never re-derive them.
    /// </summary>
    public static IReadOnlyList<DiffEdit> ToEdits(DiffPlan plan)
    {
        if (!plan.HasChanges) return [];

        var old = plan.OldText.Split('\n');

        // lineOffset[i] = offset of line i's first character; the virtual entry at old.Length
        // overshoots by one (no '\n' after the last line) and is never dereferenced — hunks
        // reaching EOF clamp to OldText.Length instead.
        var lineOffset = new int[old.Length + 1];
        for (var i = 0; i < old.Length; i++)
            lineOffset[i + 1] = lineOffset[i] + old[i].Length + 1;

        var edits = new List<DiffEdit>(plan.Hunks.Count);
        foreach (var hunk in plan.Hunks)
        {
            var count = hunk.OldLines.Count;

            if (count == 0 && hunk.OldStart >= old.Length)
            {
                // Append after a last line that has no trailing '\n'.
                edits.Add(new DiffEdit(hunk.Index, plan.OldText.Length, plan.OldText.Length,
                    "\n" + string.Join("\n",hunk.NewLines)));
                continue;
            }

            if (count == 0)
            {
                // Insertion before an existing line — the block carries its own trailing '\n'.
                edits.Add(new DiffEdit(hunk.Index, lineOffset[hunk.OldStart], lineOffset[hunk.OldStart],
                    string.Join("\n",hunk.NewLines) + "\n"));
                continue;
            }

            var reachesEof = hunk.OldStart + count == old.Length;
            var start      = lineOffset[hunk.OldStart];
            var end        = reachesEof ? plan.OldText.Length : lineOffset[hunk.OldStart + count];
            var newText    = hunk.NewLines.Count == 0
                ? string.Empty
                : string.Join("\n",hunk.NewLines) + (reachesEof ? string.Empty : "\n");

            // Deleting the trailing lines outright must also swallow the '\n' that used to
            // precede them, or the file would keep a dangling final newline.
            if (reachesEof && hunk.NewLines.Count == 0 && hunk.OldStart > 0)
                start--;

            edits.Add(new DiffEdit(hunk.Index, start, end, newText));
        }
        return edits;
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
