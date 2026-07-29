using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

// Character-offset projection of the diff plan (what the VS Code adapter feeds into a
// WorkspaceEdit). Locked invariant: for EVERY subset of hunks, applying the subset's edits
// against OldText produces exactly InlineDiffPlanner.Apply(plan, subset) — including the
// EOF edge cases (missing trailing newline, deletions/insertions touching the last line).
public class InlineDiffPlannerToEditsTests
{
    /// <summary>Applies the accepted edits (already position-ordered, non-overlapping) to
    /// <paramref name="oldText"/> the way an editor would.</summary>
    private static string ApplyEdits(string oldText, IEnumerable<DiffEdit> edits, IReadOnlyCollection<int> accepted)
    {
        var sb = new System.Text.StringBuilder();
        var cursor = 0;
        foreach (var edit in edits.Where(e => accepted.Contains(e.Index)))
        {
            sb.Append(oldText, cursor, edit.Start - cursor);
            sb.Append(edit.NewText);
            cursor = edit.End;
        }
        sb.Append(oldText, cursor, oldText.Length - cursor);
        return sb.ToString();
    }

    /// <summary>Asserts the locked invariant over every subset of the plan's hunks.</summary>
    private static void AssertAllSubsetsMatch(string oldText, string newText)
    {
        var plan  = InlineDiffPlanner.Plan(oldText, newText);
        var edits = InlineDiffPlanner.ToEdits(plan);

        Assert.Equal(plan.Hunks.Count, edits.Count);

        // Edits must be position-ordered and non-overlapping so a WorkspaceEdit accepts them.
        for (var i = 1; i < edits.Count; i++)
            Assert.True(edits[i - 1].End <= edits[i].Start,
                $"edits {i - 1} and {i} overlap for '{oldText}' → '{newText}'");

        var n = plan.Hunks.Count;
        for (var mask = 0; mask < (1 << n); mask++)
        {
            var subset = plan.Hunks.Where((_, i) => (mask & (1 << i)) != 0)
                                   .Select(h => h.Index).ToArray();
            Assert.Equal(InlineDiffPlanner.Apply(plan, subset), ApplyEdits(oldText, edits, subset));
        }
    }

    [Fact]
    public void ToEdits_NoChanges_IsEmpty()
        => Assert.Empty(InlineDiffPlanner.ToEdits(InlineDiffPlanner.Plan("a\nb", "a\nb")));

    [Fact]
    public void ToEdits_InsertionAndReplacement_AllSubsetsMatch()
        => AssertAllSubsetsMatch(
            "using System;\n\nclass A\n{\n    void M()\n    {\n        DoWork();\n    }\n}",
            "using System;\nusing System.Linq;\n\nclass A\n{\n    void M()\n    {\n        DoWorkSafely();\n    }\n}");

    [Fact]
    public void ToEdits_CrlfText_AllSubsetsMatch()
        => AssertAllSubsetsMatch("line1\r\nline2\r\nline3", "line1\r\nCHANGED\r\nline3");

    [Fact]
    public void ToEdits_MidFileDeletion_AllSubsetsMatch()
        => AssertAllSubsetsMatch("a\nb\nc", "a\nc");

    [Fact]
    public void ToEdits_DeletionOfLastLine_NoTrailingNewline_AllSubsetsMatch()
        => AssertAllSubsetsMatch("a\nb\nc", "a\nb");

    [Fact]
    public void ToEdits_DeletionOfLastLine_WithTrailingNewline_AllSubsetsMatch()
        => AssertAllSubsetsMatch("a\nb\n", "a\n");

    [Fact]
    public void ToEdits_TrailingNewlineRemoved_AllSubsetsMatch()
        => AssertAllSubsetsMatch("a\nb\n", "a\nb");

    [Fact]
    public void ToEdits_TrailingNewlineAdded_AllSubsetsMatch()
        => AssertAllSubsetsMatch("a\nb", "a\nb\n");

    [Fact]
    public void ToEdits_AppendAfterLastLineWithoutNewline_AllSubsetsMatch()
        => AssertAllSubsetsMatch("a", "a\nb");

    [Fact]
    public void ToEdits_InsertionAtStart_AllSubsetsMatch()
        => AssertAllSubsetsMatch("b\nc", "a\nb\nc");

    [Fact]
    public void ToEdits_FullRewrite_AllSubsetsMatch()
        => AssertAllSubsetsMatch("a\nb\nc", "x\ny");

    [Fact]
    public void ToEdits_EmptyToContent_AllSubsetsMatch()
        => AssertAllSubsetsMatch("", "x\ny");

    [Fact]
    public void ToEdits_ContentToEmpty_AllSubsetsMatch()
        => AssertAllSubsetsMatch("x\ny", "");

    [Fact]
    public void ToEdits_LastLineReplacement_NoTrailingNewline_AllSubsetsMatch()
        => AssertAllSubsetsMatch("a\nb", "a\nc");

    [Fact]
    public void ToEdits_MultipleScatteredHunks_AllSubsetsMatch()
        => AssertAllSubsetsMatch("a\nb\nc\nd\ne\nf", "A\nb\nc2\nc3\nd\nf\ng");

    [Fact]
    public void ToEdits_CrlfDeletionAtEof_AllSubsetsMatch()
        => AssertAllSubsetsMatch("line1\r\nline2\r\nline3", "line1\r\nline2");
}
