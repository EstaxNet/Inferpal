using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

// Foundation of the inline diff preview: the positional hunk plan and its selective application.
// Locked invariants: accept-all reproduces the new text byte-for-byte, reject-all the old text.
public class InlineDiffPlannerTests
{
    private const string Old = "using System;\n\nclass A\n{\n    void M()\n    {\n        DoWork();\n    }\n}";
    private const string New = "using System;\nusing System.Linq;\n\nclass A\n{\n    void M()\n    {\n        DoWorkSafely();\n    }\n}";

    private static int[] AllHunks(DiffPlan plan) => plan.Hunks.Select(h => h.Index).ToArray();

    [Fact]
    public void Plan_IdenticalTexts_HasNoChanges()
    {
        var plan = InlineDiffPlanner.Plan(Old, Old);

        Assert.False(plan.HasChanges);
        Assert.Equal(Old, InlineDiffPlanner.Apply(plan, []));
    }

    [Fact]
    public void Plan_SeparatedChanges_ProduceSeparateHunks()
    {
        var plan = InlineDiffPlanner.Plan(Old, New);

        Assert.Equal(2, plan.Hunks.Count);
        Assert.Empty(plan.Hunks[0].OldLines);                        // pure insertion (using line)
        Assert.Equal(["using System.Linq;"], plan.Hunks[0].NewLines);
        Assert.Equal(["        DoWork();"],       plan.Hunks[1].OldLines);
        Assert.Equal(["        DoWorkSafely();"], plan.Hunks[1].NewLines);
    }

    [Fact]
    public void Apply_AllHunks_ReproducesNewTextExactly()
    {
        var plan = InlineDiffPlanner.Plan(Old, New);

        Assert.Equal(New, InlineDiffPlanner.Apply(plan, AllHunks(plan)));
    }

    [Fact]
    public void Apply_NoHunks_ReproducesOldTextExactly()
    {
        var plan = InlineDiffPlanner.Plan(Old, New);

        Assert.Equal(Old, InlineDiffPlanner.Apply(plan, []));
    }

    [Fact]
    public void Apply_SubsetOfHunks_MergesOnlyThose()
    {
        var plan = InlineDiffPlanner.Plan(Old, New);

        var onlyUsing = InlineDiffPlanner.Apply(plan, [1]);
        Assert.Contains("using System.Linq;", onlyUsing);
        Assert.Contains("DoWork();", onlyUsing);                     // second hunk rejected
        Assert.DoesNotContain("DoWorkSafely", onlyUsing);

        var onlyRename = InlineDiffPlanner.Apply(plan, [2]);
        Assert.DoesNotContain("System.Linq", onlyRename);
        Assert.Contains("DoWorkSafely();", onlyRename);
    }

    [Fact]
    public void Plan_DeletionOnly_YieldsEmptyNewLines()
    {
        var plan = InlineDiffPlanner.Plan("a\nb\nc", "a\nc");

        var hunk = Assert.Single(plan.Hunks);
        Assert.Equal(["b"], hunk.OldLines);
        Assert.Empty(hunk.NewLines);
        Assert.Equal("a\nc",   InlineDiffPlanner.Apply(plan, [1]));
        Assert.Equal("a\nb\nc", InlineDiffPlanner.Apply(plan, []));
    }

    [Fact]
    public void Plan_CrlfText_RoundTripsByteIdentically()
    {
        const string oldCrlf = "line1\r\nline2\r\nline3";
        const string newCrlf = "line1\r\nCHANGED\r\nline3";

        var plan = InlineDiffPlanner.Plan(oldCrlf, newCrlf);

        Assert.Equal(newCrlf, InlineDiffPlanner.Apply(plan, AllHunks(plan)));
        Assert.Equal(oldCrlf, InlineDiffPlanner.Apply(plan, []));
    }

    [Fact]
    public void Plan_OversizedTexts_FallBackToSingleAllOrNothingHunk()
    {
        var oldBig = string.Join('\n', Enumerable.Range(0, 2500).Select(i => $"line {i}"));
        var newBig = oldBig.Replace("line 42", "line 42 changed");

        var plan = InlineDiffPlanner.Plan(oldBig, newBig);

        Assert.Single(plan.Hunks);
        Assert.Equal(newBig, InlineDiffPlanner.Apply(plan, [1]));
        Assert.Equal(oldBig, InlineDiffPlanner.Apply(plan, []));
    }

    [Fact]
    public void Plan_HunkPositions_MatchTheirTexts()
    {
        var plan = InlineDiffPlanner.Plan(Old, New);

        foreach (var hunk in plan.Hunks)
        {
            var oldLines = plan.OldText.Split('\n');
            var newLines = plan.NewText.Split('\n');
            Assert.Equal(hunk.OldLines, oldLines.Skip(hunk.OldStart).Take(hunk.OldLines.Count));
            Assert.Equal(hunk.NewLines, newLines.Skip(hunk.NewStart).Take(hunk.NewLines.Count));
        }
    }

    [Fact]
    public void Apply_ReorderedLines_KeepsBothInvariants()
    {
        // Degenerate case: swap — the plan may shape hunks either way, the invariants must hold.
        var plan = InlineDiffPlanner.Plan("a\nb", "b\na");

        Assert.Equal("b\na", InlineDiffPlanner.Apply(plan, AllHunks(plan)));
        Assert.Equal("a\nb", InlineDiffPlanner.Apply(plan, []));
    }
}
