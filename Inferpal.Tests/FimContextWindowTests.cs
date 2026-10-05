using System.IO;
using System.Text.RegularExpressions;
using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  The ghost-text context window is bounded in characters, not only in lines.
//
//  Visual Studio read 64 lines before the caret and 16 after, with no character cap: one line of
//  minified script or of a generated literal is hundreds of kilobytes, copied on devenv's UI thread at
//  every pause and sent whole. VS Code already capped at 4 000 / 1 500 characters.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class FimContextWindowTests
{
    [Fact]
    public void OneHugeLine_IsReadOnlyUpToTheCaps()
    {
        const int line = 1_000_000;     // a single minified line, caret in the middle
        var (start, end) = FimContextBuilder.Window(firstLineStart: 0, cursor: line / 2, lastLineEnd: line);

        Assert.Equal(FimContextBuilder.MaxPrefixChars, line / 2 - start);
        Assert.Equal(FimContextBuilder.MaxSuffixChars, end - line / 2);
    }

    [Fact]
    public void OrdinaryLines_AreReadWhole()
    {
        // Reference arm: 64 short lines before and 16 after fit under the caps — the line budget decides.
        var (start, end) = FimContextBuilder.Window(firstLineStart: 1_000, cursor: 3_000, lastLineEnd: 3_800);

        Assert.Equal((1_000, 3_800), (start, end));
    }

    [Fact]
    public void VisualStudio_ReadsThroughTheWindow()
    {
        var controller = InProcSource("Inferpal.InProc/GhostText/GhostTextController.cs");

        Assert.Contains("FimContextBuilder.Window(", controller);
        Assert.DoesNotContain("Span.FromBounds(firstLine.Start.Position, cursor)", controller);
    }

    [Theory]
    [InlineData("MAX_PREFIX_CHARS", FimContextBuilder.MaxPrefixChars)]
    [InlineData("MAX_SUFFIX_CHARS", FimContextBuilder.MaxSuffixChars)]
    public void VsCode_KeepsTheSameWindow(string name, int expected)
    {
        var ts = WebviewRebuildTests.TsCode("inlineCompletions.ts");
        var match = Regex.Match(ts, $@"const {name} = ([0-9_]+);");

        Assert.True(match.Success, $"{name} is no longer declared in inlineCompletions.ts");   // WITNESS
        Assert.Equal(expected, int.Parse(match.Groups[1].Value.Replace("_", "")));
    }

    private static string InProcSource(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        var path = Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"{relative} is gone — this guard checks nothing any more.");
        return ConventionCoverageTests.CodeOnly(path);
    }
}
