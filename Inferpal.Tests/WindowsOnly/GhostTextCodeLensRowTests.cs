using System.IO;
using System.Text.RegularExpressions;
using Inferpal.GhostText;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ A line under CodeLens carries space above its text ("N references" over a declaration). A row drawn from the line's
/// box (<c>Top</c>, <c>Height</c>, <c>Bottom</c>) lands one row up, on that space: ghost text and the inline diff preview
/// draw from the line's text only.
/// </summary>
public class GhostTextCodeLensRowTests
{
    private static string Root => ConventionCoverageTests.RepoRoot();

    [Fact]
    public void TheRowsOfALine_StartAtItsText_OneTextHeightApart()
    {
        // A declaration under CodeLens: its box starts at 100, its text at 116.
        var text = new LineBox(TextTop: 116, TextHeight: 20);
        Assert.Equal(116, text.RowTop(0));
        Assert.Equal(136, text.RowTop(1));
        Assert.Equal(136, text.TextBottom);
    }

    [Theory]
    [InlineData("GhostTextAdornment.cs")]
    [InlineData("InlineDiffAdornment.cs")]
    [InlineData("LineBox.cs")]
    public void NoRowOfTheEditorAdornments_ComesFromALinesBox(string file)
    {
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(Root, "Inferpal.InProc", "GhostText", file));
        // Witness: the file still reads the editor's geometry, through the one translation.
        Assert.Contains(file == "LineBox.cs" ? "TextTop" : "LineBoxes.Of(", code, StringComparison.Ordinal);
        // The property, whatever the variable: no member Top, Height or Bottom is read here — TextTop, TextHeight and
        // the viewport's own members are other names.
        var boxRead = new Regex(@"\.(Top|Height|Bottom)\b");
        Assert.True(!boxRead.IsMatch(code), $"{file} reads a line's box: {boxRead.Match(code).Value}");
    }
}
