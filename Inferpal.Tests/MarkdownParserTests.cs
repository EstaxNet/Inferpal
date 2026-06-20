using System.Linq;
using Inferpal.Services;
using Inferpal.ToolWindow;
using Xunit;

namespace Inferpal.Tests;

public class MarkdownParserTests
{
    // The non-breaking space used by ProcessList to indent nested list items.
    private const string Nbsp = " ";

    // Concatenated plain text of a block's inline runs (what the user actually sees rendered).
    private static string InlineText(MarkdownBlock b) =>
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
}
