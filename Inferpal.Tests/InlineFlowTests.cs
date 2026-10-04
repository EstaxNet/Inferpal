#if WINDOWS
using System.Collections;
using System.Reflection;
#endif
using System.IO;
using System.Text.RegularExpressions;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio's chat lays a paragraph out as a WrapPanel of TextBlocks (Remote UI cannot bind a TextBlock's
/// inlines), and a WrapPanel only ends a line BETWEEN two children. One child per formatted run made every run boundary
/// the only place a line could end: a run that did not fit the rest of the line dropped whole to the next one — the end
/// of a sentence under its code span, a bullet alone above a long identifier, the text after it back under the bullet —
/// and a hard line break was a taller child, not a new line ("Test Results:All"). <see cref="InlineFlow"/> cuts a
/// paragraph where a line may end; the list marker has a column of its own.
/// </summary>
public class InlineFlowTests
{
    private static InlineRunModel T(string text) => new() { Text = text };
    private static InlineRunModel C(string text) => new() { Text = text, IsCode = true };
    private static InlineRunModel B(string text) => new() { Text = text, IsBold = true };

    [Fact]
    public void AParagraphThatMixesFormats_IsCutAtEveryPlaceALineMayEnd()
    {
        var pieces = InlineFlow.Pieces([T("Validation added to "), C("OrderService.ApplyDiscount"), T(", and tests created.")]);

        Assert.Equal(
            new[] { "Validation ", "added ", "to ", "OrderService.ApplyDiscount", ", ", "and ", "tests ", "created." },
            pieces.Select(p => p.Text));
        Assert.Equal(new[] { false, false, false, true, false, false, false, false }, pieces.Select(p => p.IsCode));
    }

    [Theory]
    [InlineData("Argument validation has been added to ", "OrderService.ApplyDiscount", ", and xUnit tests  were run.")]
    [InlineData("  leading spaces, then ", "a b  c", " trailing ")]
    [InlineData("", "ApplyDiscount_NegativePrice_ThrowsArgumentOutOfRangeException", ": Validates negative price.")]
    public void NoCharacterIsLostOrAdded(string before, string code, string after)
    {
        var pieces = InlineFlow.Pieces([T(before), C(code), T(after)]);

        Assert.Equal(before + code + after, string.Concat(pieces.Select(p => p.Text)));
        Assert.DoesNotContain(pieces, p => p.Text.Length == 0);
    }

    [Fact]
    public void APieceNeverHoldsAPlaceWhereALineMayEnd()
    {
        var pieces = InlineFlow.Pieces([B("Created"), T(" the test class with six tests in "), C("Shop.Tests project file"), T(".")]);

        Assert.DoesNotContain(pieces, p => Regex.IsMatch(p.Text, @"[ \t]\S"));
    }

    [Fact]
    public void AParagraphOfUniformText_StaysOnePiece()
    {
        // Reference arm: a soft line break joins runs of the same formatting, and one TextBlock wraps inside itself.
        var pieces = InlineFlow.Pieces([T("All six tests passed"), T(" "), T("on the first run.")]);

        Assert.Equal("All six tests passed on the first run.", Assert.Single(pieces).Text);
    }

    [Fact]
    public void AHardLineBreak_IsABreakPiece_NeverATextHoldingANewline()
    {
        var pieces = InlineFlow.Pieces([B("Test Results"), T(":"), T("\n"), T("All 6 tests passed")]);

        Assert.Equal(new[] { "Test ", "Results", ":", "", "All ", "6 ", "tests ", "passed" }, pieces.Select(p => p.Text));
        Assert.Equal(3, pieces.FindIndex(p => p.IsBreak));
        Assert.Single(pieces, p => p.IsBreak);

        var uniform = InlineFlow.Pieces([T("Run it with Test Explorer\nor from the terminal.")]);
        Assert.Contains(uniform, p => p.IsBreak);
        Assert.DoesNotContain(uniform, p => p.Text.Contains('\n'));
    }

    [Fact]
    public void ACodeSpanCutIntoWords_IsPaddedOnlyAtItsOuterEdges()
    {
        var pieces = InlineFlow.Pieces([T("Run "), C("dotnet test --no-build"), T(" then "), C("git")]);
        var code   = pieces.Where(p => p.IsCode).Select(p => (p.Text, p.StartsSpan, p.EndsSpan)).ToList();

        Assert.Equal(
            new[] { ("dotnet ", true, false), ("test ", false, false), ("--no-build", false, true), ("git", true, true) },
            code);
    }

    [Fact]
    public void ANonBreakingSpace_DoesNotEndAWord()
    {
        var pieces = InlineFlow.Pieces([T("a 20\u00A0% discount "), C("x")]);

        Assert.Contains("20\u00A0% ", pieces.Select(p => p.Text));
    }

    [Fact]
    public void AListItemsMarker_IsNotPartOfItsText()
    {
        var blocks = MarkdownParser.Parse("1. **Created** the tests:\n   - `ApplyDiscount_ValidInputs_ReturnsCorrectDiscount`: Verifies it.\n");

        var numbered = Assert.Single(blocks, b => b.Type == "numbered_item");
        var bullet   = Assert.Single(blocks, b => b.Type == "bullet_item");
        Assert.Equal("1.", numbered.Marker);
        Assert.Equal("\u00A0\u00A0\u00A0•", bullet.Marker);   // the nesting indent travels with the marker
        Assert.Equal("ApplyDiscount_ValidInputs_ReturnsCorrectDiscount: Verifies it.", string.Concat(bullet.Inlines.Select(r => r.Text)));
        Assert.StartsWith("\u00A0\u00A0\u00A0• ", bullet.Text);   // the plain-text fallback keeps it
        Assert.All(blocks.Where(b => b.Type == "paragraph"), b => Assert.Equal("", b.Marker));
    }

    [Fact]
    public void TheVisualStudioView_BindsTheMarkerTheBreakAndTheSpanEdges()
    {
        // The marker left the inlines: a view that stops binding it draws list items without their bullet or number.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var xaml = File.ReadAllText(Path.Combine(dir!.FullName, "Inferpal", "ToolWindow", "InferpalToolWindowContent.xaml"));

        Assert.Contains("Text=\"{Binding Marker}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding IsBreak}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding StartsSpan}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding EndsSpan}\"", xaml, StringComparison.Ordinal);
    }

#if WINDOWS
    // ── The Visual Studio bubble: the view model's item, built by reflection (its base type is the SDK's) ──

    private static readonly Type ItemType =
        typeof(Inferpal.ToolWindow.VsThemeDetector).Assembly.GetType("Inferpal.ToolWindow.ChatMessageItem", throwOnError: true)!;

    private static object? Get(object target, string property) =>
        target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);

    [Fact]
    public void InVisualStudio_AnAnswersParagraphs_ReachTheViewAsWords()
    {
        var answer = "Validation added to `OrderService.ApplyDiscount`, and tests created.\n\n"
                   + "- `ApplyDiscount_NegativePrice_ThrowsArgumentOutOfRangeException`: Validates negative price throws.\n\n"
                   + "Run it with **Test Explorer**  \nor from the terminal.";
        var item = ItemType.GetMethod("AssistantMsg", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!
                           .Invoke(null, [answer])!;
        var blocks = ((IList)Get(item, "Blocks")!).Cast<object>().ToList();
        List<(string Text, bool IsBreak)> Pieces(object block) =>
            ((IList)Get(block, "Inlines")!).Cast<object>().Select(r => ((string)Get(r, "Text")!, (bool)Get(r, "IsBreak")!)).ToList();

        var paragraph = Pieces(blocks[0]);
        Assert.Equal("Validation added to OrderService.ApplyDiscount, and tests created.", string.Concat(paragraph.Select(p => p.Text)));
        Assert.DoesNotContain(paragraph, p => Regex.IsMatch(p.Text, @"[ \t]\S"));

        var bullet = blocks[1];
        Assert.Equal("•", Get(bullet, "Marker"));
        Assert.DoesNotContain(Pieces(bullet), p => p.Text.Contains('•'));
        Assert.Equal(": ", Pieces(bullet)[1].Text);

        Assert.Contains(Pieces(blocks[2]), p => p.IsBreak);
    }
#endif
}
