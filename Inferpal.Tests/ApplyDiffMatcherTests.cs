using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// Resolution of apply_diff edits: exact / occurrence modes / whitespace-tolerant fuzzy fallback.
public class ApplyDiffMatcherTests
{
    [Fact]
    public void UniqueExactMatch_Replaces()
    {
        var r = ApplyDiffMatcher.Resolve("a\nfoo\nb", "foo", "bar", null);
        Assert.Equal("a\nbar\nb", r.Modified);
        Assert.Equal(1, r.Count);
        Assert.False(r.Fuzzy);
    }

    [Fact]
    public void MultipleMatches_Unique_IsAmbiguous()
    {
        var r = ApplyDiffMatcher.Resolve("x\nx", "x", "y", null);
        Assert.Null(r.Modified);
        Assert.Equal(2, r.Count);
    }

    [Fact]
    public void MultipleMatches_First_ReplacesOnlyFirst()
    {
        var r = ApplyDiffMatcher.Resolve("x x x", "x", "y", "first");
        Assert.Equal("y x x", r.Modified);
    }

    [Fact]
    public void MultipleMatches_All_ReplacesEvery()
    {
        var r = ApplyDiffMatcher.Resolve("x x x", "x", "y", "all");
        Assert.Equal("y y y", r.Modified);
        Assert.Equal(3, r.Count);
    }

    [Fact]
    public void NotFound_ReturnsNullWithZeroCount()
    {
        var r = ApplyDiffMatcher.Resolve("hello world", "nope", "x", null);
        Assert.Null(r.Modified);
        Assert.Equal(0, r.Count);
    }

    [Fact]
    public void Fuzzy_IndentationDifference_StillMatches()
    {
        // File uses 4-space indent; the model supplied the lines without leading indentation.
        var file = "class C\n{\n    int x = 1;\n    int y = 2;\n}";
        var old  = "int x = 1;\nint y = 2;";
        var @new = "int x = 10;\nint y = 20;";

        var r = ApplyDiffMatcher.Resolve(file, old, @new, null);

        Assert.True(r.Fuzzy);
        Assert.Equal("class C\n{\nint x = 10;\nint y = 20;\n}", r.Modified);
    }

    [Fact]
    public void Fuzzy_CrlfAndTrailingSpaces_StillMatches()
    {
        // File uses CRLF + trailing spaces; the model supplied a clean LF multi-line block.
        var file = "foo  \r\nbar\r\nbaz";
        var r = ApplyDiffMatcher.Resolve(file, "foo\nbar", "REPLACED", null);

        Assert.True(r.Fuzzy);                          // exact fails on \r\n / trailing spaces
        Assert.Contains("REPLACED", r.Modified);
        Assert.DoesNotContain("bar", r.Modified);
    }

    [Fact]
    public void Fuzzy_AmbiguousBlock_DoesNotApply()
    {
        // Two indentation-only variants of the same trimmed block → not unique → no fuzzy edit.
        var file = "  foo\n    foo";
        var r = ApplyDiffMatcher.Resolve(file, "foo", "bar", null);

        Assert.Null(r.Modified);   // exact has 2 matches → ambiguous, fuzzy not reached
        Assert.Equal(2, r.Count);
    }
}
