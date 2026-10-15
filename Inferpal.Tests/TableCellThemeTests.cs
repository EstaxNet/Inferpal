using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio: a Markdown table follows the theme, like the rest of the answer.
/// </summary>
/// <remarks>
/// The table's cells were coloured once, when the answer was parsed; a theme switch re-coloured the text and the links of
/// every block and left the cells — and the table's border — in the colours of the other theme: light text on the light
/// theme's ground, a table that could not be read. A block's colours now have one writer, called by both.
/// </remarks>
public sealed class TableCellThemeTests
{
    private static string Code(string file) => ConventionCoverageTests.CodeOnly(
        Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal", "ToolWindow", file));

    [Fact]
    public void TheBlockThemeWriter_ColoursTheTableToo()
    {
        var block = Code("MarkdownBlock.cs");
        Assert.Contains("ThemeTableBorder = tableBorder;", block, StringComparison.Ordinal);
        Assert.Contains("cell.ThemeText = text;", block, StringComparison.Ordinal);
        Assert.Contains("cell.ThemeBg   = cell.IsHeader ? tableHeaderBg : \"Transparent\";", block, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlocksColours_HaveOneWriter_CalledAtTheParseAndAtTheThemeSwitch()
    {
        Assert.Contains("b.ApplyTheme(", Code("ChatMessageItem.cs"), StringComparison.Ordinal);                  // parsed
        Assert.Contains("b.ApplyTheme(", Code("InferpalToolWindowData.UiHelpers.cs"), StringComparison.Ordinal); // switched

        // No other site colours a block, a run or a cell on its own: that is how the two writers drifted.
        var others = ConventionCoverageTests.ViewModelSources()
            .Where(f => !f.EndsWith("MarkdownBlock.cs", StringComparison.Ordinal))
            .SelectMany(f => Regex.Matches(ConventionCoverageTests.CodeOnly(f),
                                           @"\b(?:b|block|cell|run)\.(?:ThemeText|ThemeCodeText|ThemeCodeBg|ThemeCodeBorder|ThemeTableBorder|ThemeBg|Foreground)\s*=(?!=)")
                                  .Select(m => $"{Path.GetFileName(f)}: {m.Value}"))
            .ToList();
        Assert.True(others.Count == 0, "a block is coloured outside MarkdownBlock.ApplyTheme:\n" + string.Join("\n", others));
    }
}
