using System.Linq;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

public class MarkdownParserTests
{
    // The non-breaking space used by ProcessList to indent nested list items.
    private const string Nbsp = " ";

    // Concatenated plain text of a block's inline runs (what the user actually sees rendered).
    private static string InlineText(MarkdownBlockModel b) =>
        string.Concat(b.Inlines.Select(r => r.Text));

    [Fact]
    public void FlatOrderedList_ProducesOneNumberedItemPerEntry()
    {
        var blocks = MarkdownParser.Parse("1. un\n2. deux\n3. trois");

        var items = blocks.Where(b => b.Type == "numbered_item").ToList();
        Assert.Equal(3, items.Count);
        Assert.StartsWith("1.", items[0].Text);
        Assert.Contains("deux", items[1].Text);
    }

    [Fact]
    public void BulletItem_KeepsTextAfterBoldColonOnSameLine()
    {
        // Sanity: inline text following "**bold** :" on the same line must survive.
        var blocks = MarkdownParser.Parse("- **Titre** : description detaillee ici");

        var item = Assert.Single(blocks, b => b.Type == "bullet_item");
        Assert.Contains("description detaillee ici", InlineText(item));
        Assert.True(item.HasInlines);
    }

    [Fact]
    public void NestedBulletsUnderNumberedItem_AreNotDropped()
    {
        // Regression: a ListBlock nested inside a list item used to be silently dropped, so detail
        // a model formatted as sub-bullets under a "**Title** :" header vanished, leaving only the
        // bare header line. The nested items must now be emitted as their own blocks.
        var md =
            "1. **Installation** :\n" +
            "   - telecharger le paquet\n" +
            "   - lancer le programme\n" +
            "2. **Configuration** :\n" +
            "   - editer le fichier\n";

        var blocks = MarkdownParser.Parse(md);

        // The two numbered headers survive.
        Assert.Contains(blocks, b => b.Type == "numbered_item" && b.Text.Contains("Installation"));
        Assert.Contains(blocks, b => b.Type == "numbered_item" && b.Text.Contains("Configuration"));

        // The nested detail (previously dropped) is present as bullet items.
        Assert.Contains(blocks, b => b.Type == "bullet_item" && b.Text.Contains("telecharger le paquet"));
        Assert.Contains(blocks, b => b.Type == "bullet_item" && b.Text.Contains("lancer le programme"));
        Assert.Contains(blocks, b => b.Type == "bullet_item" && b.Text.Contains("editer le fichier"));
    }

    [Fact]
    public void NestedItem_IsIndented_TopLevelItem_IsNot()
    {
        var blocks = MarkdownParser.Parse("1. parent\n   - enfant\n");

        var parent = Assert.Single(blocks, b => b.Type == "numbered_item" && b.Text.Contains("parent"));
        var nested = Assert.Single(blocks, b => b.Type == "bullet_item" && b.Text.Contains("enfant"));

        Assert.False(parent.Text.StartsWith(Nbsp));   // depth 0 → no indent
        Assert.StartsWith(Nbsp, nested.Text);         // depth 1 → non-breaking-space indent
    }

    [Fact]
    public void DeeplyNestedList_RecursesAllLevels()
    {
        var md =
            "- alpha\n" +
            "  - beta\n" +
            "    - gamma\n";

        var blocks = MarkdownParser.Parse(md);

        Assert.Contains(blocks, b => b.Type == "bullet_item" && b.Text.Contains("alpha"));
        Assert.Contains(blocks, b => b.Type == "bullet_item" && b.Text.Contains("beta"));
        Assert.Contains(blocks, b => b.Type == "bullet_item" && b.Text.Contains("gamma"));
    }

    [Fact]
    public void NestedCodeBlockInListItem_IsEmittedNotDropped()
    {
        var md =
            "1. exemple :\n" +
            "   ```cs\n" +
            "   var x = 1;\n" +
            "   ```\n";

        var blocks = MarkdownParser.Parse(md);

        Assert.Contains(blocks, b => b.Type == "numbered_item" && b.Text.Contains("exemple"));
        Assert.Contains(blocks, b => b.Type == "code_block" && b.Text.Contains("var x = 1;"));
    }

    // ── <think> stripping ─────────────────────────────────────────────────────
    //
    // Stated for as long as the parser has existed, and tested by nobody. What it costs when it
    // breaks is not subtle: the model's raw chain of thought is rendered in the chat bubble AND
    // lands in the exported conversation, for every reasoning model (qwen3, deepseek-r1,
    // magistral...). Visible - but "my export contains the reasoning" is not a symptom that points
    // at this regex.

    [Fact]
    public void Parse_RemovesThinkBlocks_AndKeepsWhatSurroundsThem()
    {
        var blocks = MarkdownParser.Parse("before\n<think>secret reasoning</think>\nafter");
        var text   = string.Join("\n", blocks.Select(b => b.Text));

        Assert.DoesNotContain("secret reasoning", text);
        Assert.DoesNotContain("<think>", text);
        Assert.Contains("before", text);
        Assert.Contains("after", text);
    }

    [Theory]
    // Several blocks: the pattern is non-greedy, it must not swallow what sits between them.
    [InlineData("<think>a</think>keep<think>b</think>", "keep")]
    // Multi-line: models emit their reasoning over dozens of lines.
    [InlineData("<think>\nline 1\nline 2\n</think>visible", "visible")]
    // Case: the pattern is IgnoreCase, and models are not consistent about it.
    [InlineData("<THINK>noise</THINK>visible", "visible")]
    [InlineData("<Think>noise</Think>visible", "visible")]
    public void StripThinkTags_HandlesTheFormsModelsActuallyEmit(string content, string expected)
    {
        var stripped = MarkdownParser.StripThinkTags(content);

        Assert.Equal(expected, stripped);
    }

    [Fact]
    public void StripThinkTags_NullOrEmpty_YieldsEmpty()
    {
        Assert.Equal(string.Empty, MarkdownParser.StripThinkTags(null));
        Assert.Equal(string.Empty, MarkdownParser.StripThinkTags(""));
    }

    [Fact]
    public void AThinkOnlyMessage_ProducesNoBlockAtAll()
    {
        // What is left after stripping is empty: Parse must return an empty list rather than a
        // blank bubble. That is the "the model produced nothing but reasoning" case, which the
        // orchestrator then treats as an empty turn.
        Assert.Empty(MarkdownParser.Parse("<think>nothing but reasoning</think>"));
    }
}
