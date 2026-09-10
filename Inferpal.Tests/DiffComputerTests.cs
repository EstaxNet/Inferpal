using Inferpal.Localization;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Serialized: the renderer writes three lines of PROSE of its own ("too large", "unchanged
// lines", "more diff lines") which now go through Strings, hence through a process-wide culture.
// The structural assertions look at Prefix; a single one tests the text, and that is the one
// that switches language.
[Collection(CultureSerialCollection.Name)]
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

        // Structural: the marker's text is localized, so it follows the process culture.
        Assert.Contains(result, l => l.Prefix == "…");
    }

    [Fact]
    public void Compute_SmallFile_NoCollapse()
    {
        // 4 lines, change at index 0 — all lines within CtxLines=3 distance, no collapse
        var result = DiffComputer.Compute("a\nb\nc\nd", "A\nb\nc\nd");

        Assert.DoesNotContain(result, l => l.Prefix == "…");
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

        // The remaining count is DERIVED from the full render: hard-coding it would tie the test
        // to the exact path the LCS takes rather than to the property being measured.
        var total = DiffComputer.Compute(old, @new).Count;
        Assert.Contains((total - 10).ToString(), text);
    }

    // ── The cap is about what CHANGES, not about the size of the file ────────

    [Fact]
    public void Compute_OneLineChangedInLargeFile_ShowsTheChange()
    {
        // Measured 2026-09-10. The cap was read off the WHOLE FILE: a single changed line in a
        // 400-line source rendered a sentence instead of the change — at the approval prompt,
        // that is, on the one surface where the human sees what they are agreeing to.
        // 15% of the non-test sources in this repo (74/486) are over 300 lines.
        var old  = Enumerable.Range(0, 400).Select(i => $"line{i}").ToArray();
        var @new = old.ToArray();
        @new[200] = "CHANGED";

        var result = DiffComputer.Compute(string.Join("\n", old), string.Join("\n", @new));

        Assert.Contains(result, l => l.Prefix == "-" && l.Text == "line200");
        Assert.Contains(result, l => l.Prefix == "+" && l.Text == "CHANGED");
    }

    [Fact]
    public void Compute_OneLineChangedInLargeFile_ReportsTheUntouchedHeadAndTail()
    {
        // What is trimmed before the computation is still owed to the reader: they have to read
        // "there are 197 lines above", not believe the file starts there. CtxLines=3 on each side
        // enter the window; the rest is a collapsed run, exactly like an interior gap.
        var old  = Enumerable.Range(0, 400).Select(i => $"line{i}").ToArray();
        var @new = old.ToArray();
        @new[200] = "CHANGED";

        var result = DiffComputer.Compute(string.Join("\n", old), string.Join("\n", @new));

        var markers = result.Where(l => l.Prefix == "…").ToList();
        Assert.Equal(2, markers.Count);
        Assert.Contains("197", markers[0].Text);    // 200 identical - 3 of context
        Assert.Contains("196", markers[1].Text);    // 199 identical - 3 of context
    }

    [Fact]
    public void Compute_ChangedRegionTooLarge_StillReturnsSingleEllipsisLine()
    {
        // Witness: the cap did not disappear, it changed subject. Head and tail are identical
        // here, but the REGION that differs overflows — exactly the case it was written for
        // (the DP is O(m·n) and its table an int[m+1, n+1]).
        var old  = Enumerable.Range(0, 400).Select(i => $"line{i}").ToArray();
        var @new = old.ToArray();
        for (var i = 20; i < 380; i++) @new[i] = $"changed{i}";

        var result = DiffComputer.Compute(string.Join("\n", old), string.Join("\n", @new));

        Assert.Single(result);
        Assert.Equal("…", result[0].Prefix);
    }

    [Fact]
    public void Compute_LargeFileWindow_StaysCheapEnoughToRender()
    {
        // What the cap is really about: without the trimming, this file allocated an
        // int[8001, 8001] — 256 MB — for a one-line change. The threshold is deliberately loose
        // (a second is already unacceptable in a modal prompt): what is measured is the order of
        // magnitude.
        var old  = Enumerable.Range(0, 8000).Select(i => $"line{i}").ToArray();
        var @new = old.ToArray();
        @new[4000] = "CHANGED";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = DiffComputer.Compute(string.Join("\n", old), string.Join("\n", @new));
        sw.Stop();

        Assert.Contains(result, l => l.Prefix == "+" && l.Text == "CHANGED");
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Rendered in {sw.ElapsedMilliseconds} ms.");
    }

    // ── The lines the renderer writes ITSELF are user-facing text ────────────

    [Fact]
    public void SyntheticLines_AreLocalized()
    {
        // The renderer's three lines of prose were hard-coded — and "Fichier trop grand pour
        // afficher le diff" was in FRENCH, served as-is to all ten languages, at the approval
        // prompt. Rule 9 could not see it: its scope is the view-model and the host, and this
        // text is born in the Core and crosses the boundary inside a DiffLineModel.
        try
        {
            Strings.ApplyLanguage("en");

            var big     = string.Join("\n", Enumerable.Range(0, 400).Select(i => $"a{i}"));
            var other   = string.Join("\n", Enumerable.Range(0, 400).Select(i => $"b{i}"));
            var enLarge = DiffComputer.Compute(big, other)[0].Text;

            var old     = Enumerable.Range(0, 400).Select(i => $"line{i}").ToArray();
            var @new    = old.ToArray();
            @new[200]   = "CHANGED";
            var enSkip  = DiffComputer.Compute(string.Join("\n", old), string.Join("\n", @new))
                                      .First(l => l.Prefix == "…").Text;

            var enMore  = DiffComputer.ComputeText(big, other, maxLines: 2)!;

            Strings.ApplyLanguage("fr");

            Assert.NotEqual(enLarge, DiffComputer.Compute(big, other)[0].Text);
            Assert.NotEqual(enSkip,  DiffComputer.Compute(string.Join("\n", old), string.Join("\n", @new))
                                                 .First(l => l.Prefix == "…").Text);
            Assert.NotEqual(enMore,  DiffComputer.ComputeText(big, other, maxLines: 2)!);
        }
        finally { Strings.ApplyLanguage(null); }
    }
}
