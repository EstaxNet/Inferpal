using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

public class DiffComputerTests
{
    [Fact]
    public void Compute_IdenticalTexts_ReturnsEmpty()
    {
        var result = DiffComputer.Compute("hello\nworld", "hello\nworld");
        Assert.Empty(result);
    }

    [Fact]
    public void Compute_SingleLineReplaced_ContainsRemoveAndAdd()
    {
        var result = DiffComputer.Compute("old", "new");

        Assert.Contains(result, l => l.Prefix == "-" && l.Text == "old");
        Assert.Contains(result, l => l.Prefix == "+" && l.Text == "new");
    }

    [Fact]
    public void Compute_LineInsertedInMiddle_InsertsAddWithContext()
    {
        var result = DiffComputer.Compute("a\nb\nc", "a\nX\nb\nc");

        Assert.Contains(result, l => l.Prefix == "+" && l.Text == "X");
        // Context lines around the insertion
        Assert.Contains(result, l => l.Prefix == " ");
        // No deletions — nothing was removed
        Assert.DoesNotContain(result, l => l.Prefix == "-");
    }

    [Fact]
    public void Compute_LineRemoved_InsertsDeletion()
    {
        var result = DiffComputer.Compute("a\nb\nc", "a\nc");

        Assert.Contains(result, l => l.Prefix == "-" && l.Text == "b");
        Assert.DoesNotContain(result, l => l.Prefix == "+");
    }

    [Fact]
    public void Compute_OldTextTooLong_ReturnsSingleEllipsisLine()
    {
        var bigOld = string.Join("\n", Enumerable.Range(0, 301).Select(i => $"line{i}"));
        var result = DiffComputer.Compute(bigOld, "short");

        Assert.Single(result);
        Assert.Equal("…", result[0].Prefix);
    }

    [Fact]
    public void Compute_NewTextTooLong_ReturnsSingleEllipsisLine()
    {
        var bigNew = string.Join("\n", Enumerable.Range(0, 301).Select(i => $"line{i}"));
        var result = DiffComputer.Compute("short", bigNew);

        Assert.Single(result);
        Assert.Equal("…", result[0].Prefix);
    }

    [Fact]
    public void Compute_UnchangedBlockBetweenChanges_CollapsedWithEllipsis()
    {
        // 20 lines, first and last changed — middle 12 lines should be collapsed.
        var old = Enumerable.Range(1, 20).Select(i => $"line{i}").ToArray();
        var @new = old.ToArray();
        @new[0]  = "CHANGED_FIRST";
        @new[19] = "CHANGED_LAST";

        var result = DiffComputer.Compute(string.Join("\n", old), string.Join("\n", @new));

        Assert.Contains(result, l => l.Prefix == "…" && l.Text.Contains("unchanged"));
    }

    [Fact]
    public void Compute_SmallFile_NoCollapse()
    {
        // 4 lines, change at index 0 — all lines within CtxLines=3 distance, no collapse
        var result = DiffComputer.Compute("a\nb\nc\nd", "A\nb\nc\nd");

        Assert.DoesNotContain(result, l => l.Prefix == "…");
    }

    [Fact]
    public void Compute_AddedLine_ForegroundGreen()
    {
        var result = DiffComputer.Compute("old", "new");
        var added = result.First(l => l.Prefix == "+");

        Assert.Equal("#6DB96D", added.Foreground);
        Assert.Equal("#1A3A1A", added.Background);
    }

    [Fact]
    public void Compute_RemovedLine_ForegroundRed()
    {
        var result = DiffComputer.Compute("old", "new");
        var removed = result.First(l => l.Prefix == "-");

        Assert.Equal("#F47C7C", removed.Foreground);
        Assert.Equal("#3A1A1A", removed.Background);
    }

    // ── ComputeText (approval-prompt rendering) ──────────────────────────────

    [Fact]
    public void ComputeText_Identical_ReturnsNull()
    {
        Assert.Null(DiffComputer.ComputeText("same\ntext", "same\ntext"));
    }

    [Fact]
    public void ComputeText_RendersPrefixedLines()
    {
        var text = DiffComputer.ComputeText("old", "new");
        Assert.NotNull(text);
        Assert.Contains("-old", text);
        Assert.Contains("+new", text);
    }

    [Fact]
    public void ComputeText_CapsToMaxLinesWithMoreMarker()
    {
        var old  = string.Join("\n", Enumerable.Range(0, 60).Select(i => $"a{i}"));
        var @new = string.Join("\n", Enumerable.Range(0, 60).Select(i => $"b{i}"));   // every line changed

        var text = DiffComputer.ComputeText(old, @new, maxLines: 10);

        Assert.NotNull(text);
        Assert.Equal(11, text!.Split('\n').Length);     // 10 shown + the "more" marker line
        Assert.Contains("more diff line(s)", text);
    }
}
