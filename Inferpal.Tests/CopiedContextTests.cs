using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An edit whose replacement replays lines that stay in the file around what it replaces (pari 69): the whole page sent
/// for a fragment leaves the page's head twice. Judged on what the matcher replaced.
/// </summary>
public class CopiedContextTests
{
    private const string Counter =
        "\uFEFF@page \"/counter\"\n@rendermode InteractiveServer\n\n<PageTitle>Counter</PageTitle>\n\n<h1>Counter</h1>\n\n"
        + "<p role=\"status\">Current count: @currentCount</p>\n\n"
        + "<button class=\"btn btn-primary\" @onclick=\"IncrementCount\">Click me</button>\n\n"
        + "@code {\n    private int currentCount = 0;\n\n    private void IncrementCount()\n    {\n        currentCount++;\n    }\n}\n";

    private static CopiedContext.Copy? Judge(string file, string oldContent, string newContent, string? occurrence = null)
    {
        var r = ApplyDiffMatcher.Resolve(file, oldContent, newContent, occurrence);
        Assert.NotNull(r.Modified);
        return CopiedContext.Find(file, r.Zones, newContent);
    }

    [Fact]
    public void TheWholePage_SentForItsEnd_AndRetouched_IsACopyOfItsHead()
    {
        // Gemma 4 12B's edit: old_content from <p role…> to the end, new_content the whole page with a changed <h1>.
        var old = Counter[Counter.IndexOf("<p role", StringComparison.Ordinal)..];
        var page = "@page \"/counter\"\n@rendermode InteractiveServer\n\n<PageTitle>Counter</PageTitle>\n\n"
                 + "<h1 class=\"display-5\">Counter</h1>\n\n" + old.Replace("Click me</button>",
                     "Click me</button>\n<button class=\"btn btn-secondary\" @onclick=\"ResetCount\">Reset</button>");
        var copy = Judge(Counter, old, page);
        Assert.NotNull(copy);
        Assert.Equal(CopiedContext.Side.Head, copy.Side);
        Assert.Equal(3, copy.Lines);
        Assert.Equal("@page \"/counter\"", copy.First);
    }

    [Fact]
    public void TheWholePage_SentForAFragmentInTheMiddle_IsACopyOfItsHead()
    {
        var page = Counter.TrimStart('\uFEFF').Replace("<h1>Counter</h1>", "<h1>My counter</h1>");
        var copy = Judge(Counter, "<h1>Counter</h1>", page);
        Assert.NotNull(copy);
        Assert.Equal(CopiedContext.Side.Head, copy.Side);
    }

    [Fact]
    public void TheWholePage_SentForItsFirstLines_IsACopyOfItsTail()
    {
        var page = Counter.TrimStart('\uFEFF').Replace("@rendermode InteractiveServer", "@rendermode InteractiveAuto");
        var copy = Judge(Counter, "@rendermode InteractiveServer", page);
        Assert.NotNull(copy);
        Assert.Equal(CopiedContext.Side.Tail, copy.Side);
        Assert.Equal("<PageTitle>Counter</PageTitle>", copy.First);
    }

    [Fact]
    public void TheLinesJustBeforeTheZone_RepeatedAtTheSeam_AreACopy()
    {
        const string file = "def a():\n    x = 1\n    y = 2\n    return x + y\n";
        // old_content is the last line, new_content repeats the two before it and changes the return.
        var copy = Judge(file, "    return x + y\n", "    x = 1\n    y = 2\n    return x * y\n");
        Assert.NotNull(copy);
        Assert.Equal(CopiedContext.Side.Head, copy.Side);
        Assert.Equal(2, copy.Lines);
    }

    [Fact]
    public void TheLinesJustAfterTheZone_RepeatedAtTheSeam_AreACopy()
    {
        const string file = "first = 1\nsecond = 2\nthird = 3\nfourth = 4\n";
        var copy = Judge(file, "first = 1\n", "first = 10\nsecond = 2\nthird = 3\n");
        Assert.NotNull(copy);
        Assert.Equal(CopiedContext.Side.Tail, copy.Side);
        Assert.Equal(2, copy.Lines);
    }

    [Fact]
    public void AFragmentForAFragment_IsNoCopy()
    {
        Assert.Null(Judge(Counter, "Click me</button>",
            "Click me</button>\n<button class=\"btn btn-secondary\" @onclick=\"ResetCount\">Reset</button>"));
    }

    [Fact]
    public void ContextThatOldContentCovers_IsNoCopy()
    {
        // The model sends the context in old_content too: it rewrites it, nothing stays twice.
        const string file = "def a():\n    x = 1\n    y = 2\n    return x + y\n";
        Assert.Null(Judge(file, "    x = 1\n    y = 2\n    return x + y\n", "    x = 1\n    y = 2\n    return x * y\n"));
    }

    [Fact]
    public void RepetitiveCode_ThatTheZoneBeginsWith_IsNoCopy()
    {
        // Two identical pairs, the zone is the second one, and two rows added after it: the replacement begins as the
        // zone does (its own rows, kept once), not as a copy of the pair before it.
        const string file = "[InlineData(\"a\")]\n[InlineData(\"b\")]\n[InlineData(\"a\")]\n[InlineData(\"b\")]\npublic void T(string s) { }\n";
        Assert.Null(Judge(file, "[InlineData(\"a\")]\n[InlineData(\"b\")]\npublic void T",
                                "[InlineData(\"a\")]\n[InlineData(\"b\")]\n[InlineData(\"c\")]\n[InlineData(\"d\")]\npublic void T"));
    }

    [Fact]
    public void ClosingBracesAtASeam_AreNoCopy()
    {
        const string file = "class A\n{\n    void F()\n    {\n    }\n}\n// end\n";
        Assert.Null(Judge(file, "// end\n", "    }\n}\n// the end\n"));
    }

    [Fact]
    public void AReindentedCopy_IsStillACopy()
    {
        const string file = "def a():\n    x = 1\n    y = 2\n    return x + y\n";
        Assert.NotNull(Judge(file, "    return x + y\n", "x = 1\ny = 2\nreturn x * y\n"));
    }

    [Fact]
    public void ACrlfFile_IsJudgedLikeAnLfOne()
    {
        var crlf = Counter.Replace("\n", "\r\n");
        var page = Counter.TrimStart('\uFEFF').Replace("<h1>Counter</h1>", "<h1>My counter</h1>");
        Assert.NotNull(Judge(crlf, "<h1>Counter</h1>", page));
    }

    [Fact]
    public void AToleratedMatch_IsJudgedOnTheBlockItFound()
    {
        // old_content with another indentation: the tolerant pass finds the block, and its zone is judged.
        const string file = "def a():\n    x = 1\n    y = 2\n    return x + y\n";
        var r = ApplyDiffMatcher.Resolve(file, "  return x + y  ", "  x = 1\n  y = 2\n  return x * y", null);
        Assert.True(r.Fuzzy);
        Assert.NotNull(CopiedContext.Find(file, r.Zones, "  x = 1\n  y = 2\n  return x * y"));
    }

    [Fact]
    public void EveryOccurrence_IsJudged()
    {
        const string file = "alpha = 1\nbeta = 2\nmark()\ngamma = 3\nmark()\n";
        // occurrence "all": each zone is judged, and the file's first two lines replayed are a copy at the first one.
        Assert.NotNull(Judge(file, "mark()", "alpha = 1\nbeta = 2\nmark2()", "all"));
    }

    [Fact]
    public void AZoneThatStartsMidLine_StillSeesTheFilesHeadReplayed()
    {
        const string file = "header one\nheader two\nx = compute(1)\ny = 2\n";
        Assert.Null(Judge(file, "compute(1)", "compute(2)\nz = 3"));
        Assert.NotNull(Judge(file, "compute(1)", "header one\nheader two\nx = compute(2)"));
    }

    [Fact]
    public void AZoneThatStartsOnALineBreak_LeavesThatLineWholeBeforeIt()
    {
        const string file = "a = 1\nb = 2\n<p>x</p>\n";
        var copy = Judge(file, "\n<p>x</p>", "\na = 1\nb = 2\n<p>y</p>");
        Assert.NotNull(copy);
        Assert.Equal(2, copy.Lines);
    }

    [Fact]
    public void ARowMadeEqualToTheOneBeforeIt_IsNoCopy()
    {
        // The replacement has as many lines as the zone: it adds nothing, so nothing it holds can be there twice.
        const string file = "Assert.Equal(1, r.Count);\nAssert.Equal(\"x\", r[0]);\nAssert.Equal(1, r.Count);\nAssert.Equal(\"y\", r[0]);\n";
        Assert.Null(Judge(file, "Assert.Equal(1, r.Count);\nAssert.Equal(\"y\", r[0]);\n",
                                "Assert.Equal(1, r.Count);\nAssert.Equal(\"x\", r[0]);\n", "first"));
    }

    [Fact]
    public void TheWholePage_SentForItsFirstLines_AndRetouchedAtItsEnd_IsACopyOfWhatStaysAround()
    {
        // The mirror of Gemma's edit: old_content is the page's head, new_content the whole page with a method added at
        // the end — its head is the zone's own, its tail ends on two braces after a changed line.
        var head = Counter[..(Counter.IndexOf("<h1>Counter</h1>", StringComparison.Ordinal) + "<h1>Counter</h1>".Length)];
        var page = Counter.TrimStart('\uFEFF')
            .Replace("Click me</button>", "Click me</button>\n<button class=\"btn btn-secondary\" @onclick=\"ResetCount\">Reset</button>")
            .Replace("        currentCount++;\n    }\n}", "        currentCount++;\n    }\n\n    private void ResetCount()\n    {\n        currentCount = 0;\n    }\n}");
        var copy = Judge(Counter, head.TrimStart('\uFEFF'), page);
        Assert.NotNull(copy);
        Assert.Equal(CopiedContext.Side.Around, copy.Side);
    }

    [Fact]
    public void AMethodAddedToAClass_IsNoCopyOfTheClass()
    {
        const string file = "using System;\n\nnamespace Shop;\n\npublic class Cart\n{\n    private readonly List<decimal> _prices = new();\n\n"
                          + "    public void Add(decimal price) => _prices.Add(price);\n}\n";
        Assert.Null(Judge(file, "    public void Add(decimal price) => _prices.Add(price);\n",
            "    public void Add(decimal price) => _prices.Add(price);\n\n    public void Clear()\n    {\n        _prices.Clear();\n    }\n"));
    }

    [Fact]
    public void TheQuotedLine_IsNeverCutInsideACharacter()
    {
        // An emoji (two UTF-16 units) across the 80th unit: a lone half is not valid text in the request.
        var line = new string('a', 79) + char.ConvertFromUtf32(0x1F600) + " and more";
        var refusal = CopiedContext.Refusal(new CopiedContext.Copy(CopiedContext.Side.Head, 2, line), "a.md");
        Assert.Equal(refusal, System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(refusal)));
    }
}
