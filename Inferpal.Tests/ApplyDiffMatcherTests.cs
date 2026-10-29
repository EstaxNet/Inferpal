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
        // At the indentation the block was found at, not the model's.
        Assert.Equal("class C\n{\n    int x = 10;\n    int y = 20;\n}", r.Modified);
    }

    [Fact]
    public void Fuzzy_InPython_TheReplacementStaysInItsFunction()
    {
        var file = "def f():\n    x = 1\n    print(x)\n";
        var r = ApplyDiffMatcher.Resolve(file, "x = 1\nprint(x)", "x = 2\nif x:\n    print(x)", null);

        Assert.True(r.Fuzzy);
        Assert.Equal("def f():\n    x = 2\n    if x:\n        print(x)\n", r.Modified);
    }

    [Fact]
    public void Fuzzy_AModelThatIndentedTooFar_IsBroughtBack()
    {
        var file = "if (a)\n{\n    Foo();\n    Bar();\n}";
        var r = ApplyDiffMatcher.Resolve(file, "        Foo();\n        Bar();", "        Foo(1);\n        Bar();", null);

        Assert.Equal("if (a)\n{\n    Foo(1);\n    Bar();\n}", r.Modified);
    }

    [Fact]
    public void Fuzzy_ABlockReplacedByABlankLine_KeepsIt_LikeTheExactPass()
    {
        var file  = "a();\n    if (x)\n    {\n        Foo();\n    }\nb();\n";
        var fuzzy = ApplyDiffMatcher.Resolve(file, "if (x)\n{\n    Foo();\n}\n", "\n", null);
        var exact = ApplyDiffMatcher.Resolve(file, "    if (x)\n    {\n        Foo();\n    }\n", "\n", null);

        Assert.Equal("a();\n\nb();\n", exact.Modified);
        Assert.Equal(exact.Modified, fuzzy.Modified);
    }

    [Fact]
    public void Fuzzy_ALineWrittenLeftOfTheExtraIndentation_KeepsItsPlaceRelativeToTheOthers()
    {
        // Four spaces too many in the model's frame; its second line two spaces in: it goes as far left as it can.
        var file = "if (a)\n{\n    Foo();\n}";
        var r = ApplyDiffMatcher.Resolve(file, "        Foo();", "        Foo(1);\n  Bar();", null);

        Assert.Equal("if (a)\n{\n    Foo(1);\nBar();\n}", r.Modified);
    }

    [Fact]
    public void Fuzzy_ADeletion_LeavesNoBlankLine_LikeTheExactPass()
    {
        var file = "a();\n    if (x)\n    {\n        Foo();\n    }\nb();\n";
        var fuzzy = ApplyDiffMatcher.Resolve(file, "if (x)\n{\n    Foo();\n}\n", "", null);
        // Reference arm: the same deletion, written with the file's own indentation.
        var exact = ApplyDiffMatcher.Resolve(file, "    if (x)\n    {\n        Foo();\n    }\n", "", null);

        Assert.True(fuzzy.Fuzzy);
        Assert.False(exact.Fuzzy);
        Assert.Equal("a();\nb();\n", exact.Modified);
        Assert.Equal(exact.Modified, fuzzy.Modified);
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
