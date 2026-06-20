using Inferpal.Commands;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Covers the pure target-range resolution shared by the in-place code actions
// (Refactor / Fix / Add-docs), both context-menu and slash variants.
public class InPlaceCodeEditTests
{
    [Fact]
    public void NoSelection_TargetsWholeFile()
    {
        var doc = "line1\nline2\nline3";
        var (code, start, end, hasSel) = InPlaceCodeEdit.ResolveTarget(doc, 0, 0, selectionEmpty: true);
        Assert.Equal(doc, code);
        Assert.Equal(0, start);
        Assert.Equal(doc.Length, end);
        Assert.False(hasSel);
    }

    [Fact]
    public void Selection_TargetsOnlyTheSelectedSpan()
    {
        //            0123456789012
        var doc = "ab\nXXXX\ncd";   // select "XXXX" (offsets 3..7)
        var (code, start, end, hasSel) = InPlaceCodeEdit.ResolveTarget(doc, 3, 7, selectionEmpty: false);
        Assert.Equal("XXXX", code);
        Assert.Equal(3, start);
        Assert.Equal(7, end);
        Assert.True(hasSel);
    }

    [Fact]
    public void Selection_StartingMidIndent_ExpandsBackToLineStart()
    {
        // Line "    foo" — selection starts at 'f' (offset 4) past the 4 leading spaces.
        // The start expands back to the line start so the snippet carries its indentation.
        var doc = "    foo";
        var (code, start, _, hasSel) = InPlaceCodeEdit.ResolveTarget(doc, 4, 7, selectionEmpty: false);
        Assert.Equal("    foo", code);
        Assert.Equal(0, start);
        Assert.True(hasSel);
    }

    [Fact]
    public void EmptySpanSelection_FallsBackToWholeFile()
    {
        var doc = "abc";
        var (code, _, _, hasSel) = InPlaceCodeEdit.ResolveTarget(doc, 2, 2, selectionEmpty: false);
        Assert.Equal(doc, code);
        Assert.False(hasSel);
    }
}

// Covers the pure fence-stripping applied to in-place code-action model output
// ("Edit with AI" + the Refactor / Fix / Add-docs context-menu actions).
public class InlineEditResponseTests
{
    private static string C(string raw) => InlineEditResponse.Clean(raw).Replace("\r\n", "\n");

    [Fact]
    public void NoFence_ReturnedUnchanged()
    {
        var code = "var x = 1;\nvar y = 2;";
        Assert.Equal(code, C(code));
    }

    [Fact]
    public void LeadingAndTrailingFence_AreStripped()
    {
        var raw = "```csharp\nvar x = 1;\n```";
        Assert.Equal("var x = 1;", C(raw));
    }

    [Fact]
    public void IndentedFence_IsStripped()
    {
        // Models sometimes indent their fences when the original code is indented.
        var raw = "    ```csharp\n    var x = 1;\n    ```";
        Assert.Equal("    var x = 1;", C(raw));
    }

    [Fact]
    public void LeadingSpacesOfFirstCodeLine_ArePreserved()
    {
        // Leading spaces must survive so the reindenter can detect the base-indent delta;
        // only leading newlines are trimmed.
        var raw = "```\n        if (ok) return;\n```";
        Assert.Equal("        if (ok) return;", C(raw));
    }

    [Fact]
    public void FenceWithoutClosing_StripsOnlyOpening()
    {
        var raw = "```\nvar x = 1;";
        Assert.Equal("var x = 1;", C(raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void EmptyOrWhitespace_ReturnsEmptyish(string raw)
    {
        Assert.True(string.IsNullOrWhiteSpace(C(raw)));
    }
}
