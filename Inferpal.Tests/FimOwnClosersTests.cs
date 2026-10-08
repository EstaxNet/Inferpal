using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A completion's own closers are not a repetition of the lines after the caret — and telling them apart reads the
/// completion's brackets, so a character literal and a markup element have to be read as what they are.
/// </summary>
/// <remarks>
/// The run-on cut (<see cref="FimRunOnCompletionTests"/>) keeps a completion's last lines when they close what it
/// opened. Its bracket count took the <c>"</c> of <c>'"'</c> for the start of a string and counted nothing after it,
/// and it never counted an element: a <c>foreach</c> with a quote test, or a nested <c>&lt;div&gt;</c>, lost its own
/// closing lines — the file's braces then closed the block, and the method or the page no longer compiled.
/// </remarks>
public class FimOwnClosersTests
{
    [Fact]
    public void ABlockWithAQuoteCharacterLiteral_KeepsItsOwnClosingBraces()
    {
        // Caret at the end of "var n = 0;", inside a method of a class: "}" and "}" follow.
        var completion = "\n    foreach (var c in s)\n    {\n        if (c == '\"')\n        {\n            n++;\n        }\n"
                         + "    }\n    return n;";

        Assert.Equal(completion, FimCompletion.Finish(completion, "\n}\n}\n"));
    }

    [Theory]
    [InlineData("'{'")]
    [InlineData("'}'")]
    [InlineData("'\\''")]
    [InlineData("'\\\\'")]
    [InlineData("'('")]
    [InlineData("\"'\"")]
    public void OtherCharacterLiterals_KeepTheBlocksOwnClosingBraces(string literal)
    {
        // Reference arm: literals that never hid the braces after them keep the same answer.
        var completion = $"\n    foreach (var c in s)\n    {{\n        if (c == {literal})\n        {{\n            n++;\n        }}\n"
                         + "    }\n    return n;";

        Assert.Equal(completion, FimCompletion.Finish(completion, "\n}\n}\n"));
    }

    [Fact]
    public void NestedElements_KeepTheirOwnClosingTags()
    {
        // Caret at the end of <div class="card">; the card and the page's container close below.
        var completion = "\n  <div class=\"row\">\n    <div class=\"col\">\n      Name\n    </div>\n  </div>\n  <p>Total</p>";

        Assert.Equal(completion, FimCompletion.Finish(completion, "\n</div>\n</div>\n"));
    }

    [Fact]
    public void AnElementWrittenOverSeveralLines_IsStillOneElement()
    {
        var completion = "\n    <StackPanel\n        Orientation=\"Horizontal\">\n        <TextBlock Text=\"{Binding Name}\" />\n"
                         + "    </StackPanel>\n</Grid>\n<Button Content=\"Save\" />";

        Assert.Equal(completion, FimCompletion.Finish(completion, "\n</StackPanel>\n</Grid>\n"));
    }

    [Fact]
    public void AMarkupRunOn_IsStillCutWhereItRepeatsTheClosingTags()
    {
        // Reference arm: these closers are the page's, written again — the cut still happens.
        var raw = "\n  <h2>Title</h2>\n</div>\n</div>\n<footer>Copyright</footer>\n\nThis markup creates a card.";

        Assert.Equal("\n  <h2>Title</h2>", FimCompletion.Finish(raw, "\n</div>\n</div>\n"));
    }

    [Fact]
    public void MidLine_AnElementClosedAgain_LeavesTheLinesOwnClosingTag()
    {
        // Caret in <p>|</p>: the model closes the paragraph the line already closes.
        Assert.Equal("Hello", FimCompletion.Finish("Hello</p>", "</p>\n"));
    }

    [Fact]
    public void GenericsAndComparisons_AreNotElements()
    {
        // Reference arm: List<int> and a < b open nothing — the C# run-on cut is unchanged.
        var raw = "var xs = new List<int>();\nif (a < b) xs.Add(a);\n}\nreturn xs;\n\n// Explanation: ...";

        Assert.Equal("var xs = new List<int>();\nif (a < b) xs.Add(a);", FimCompletion.Finish(raw, "\n}\nreturn xs;\n"));
    }
}
