using System.IO;
using System.Text;
using Inferpal.Localization;
using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ <c>/review</c> answers with a verdict, and a third of an ordinary repository's source files are
/// longer than the excerpt budget. The cut was a bare <c>…(truncated)</c> for the model and nothing at
/// all for the human, who saw the file's name under the question: a review of the first two hundred
/// lines, read as a review of the file.
/// </summary>
[Collection(CultureSerialCollection.Name)]   // compares a localized label
public class CodeExcerptTests
{
    private static string Lines(int count)
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= count; i++) sb.Append("line ").Append(i.ToString("D3")).Append(" of the source file ....\n");
        return sb.ToString();
    }

    [Fact]
    public void ALongFile_IsCutOnALineEnd_AndTheMarkerNamesTheCount()
    {
        var code    = Lines(500);                       // 30 chars a line: 15,000 chars
        var excerpt = CodeExcerpt.Of(code);

        Assert.True(excerpt.IsTruncated);
        Assert.Equal(500, excerpt.TotalLines);
        var shownPart = code[..code.IndexOf($"line {excerpt.ShownLines + 1:D3}", StringComparison.Ordinal)].TrimEnd('\n');
        Assert.StartsWith(shownPart, excerpt.Text, StringComparison.Ordinal);          // whole lines, nothing cut mid-line
        Assert.Contains($"the first {excerpt.ShownLines} of 500 lines", excerpt.Text); // the model reads how much
        Assert.True(excerpt.Text.Length <= CodeExcerpt.MaxChars + 120);
    }

    [Fact]
    public void ALongFile_ShowsTheCountToTheHuman_UnderTheQuestion()
    {
        var excerpt = CodeExcerpt.Of(Lines(500));

        Assert.Equal(Strings.CodeExcerptLabel("Foo.cs", excerpt.ShownLines, 500), excerpt.Label("Foo.cs"));
        Assert.NotEqual("Foo.cs", excerpt.Label("Foo.cs"));
    }

    [Fact]
    public void AFileWithinTheBudget_IsSentWhole_AndItsLabelIsUntouched()
    {
        // Reference arm: two thirds of the files. A count under every chip is the noise that gets it ignored.
        var code    = Lines(100);
        var excerpt = CodeExcerpt.Of(code);

        Assert.False(excerpt.IsTruncated);
        Assert.Equal(code, excerpt.Text);
        Assert.Equal("Foo.cs", excerpt.Label("Foo.cs"));
    }

    [Theory]
    [InlineData("Inferpal", "ToolWindow", "InferpalToolWindowData.CodeActions.cs")]
    [InlineData("Inferpal", "Commands", "SelectionCommandBase.cs")]
    public void BothVisualStudioSites_GoThroughTheExcerpt(params string[] parts)
    {
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(), Path.Combine(parts)));

        // WITNESS: the site still reads the editor's text — the thing it caps.
        Assert.Contains("CopyToString()", code, StringComparison.Ordinal);

        Assert.Contains("CodeExcerpt.Of(", code, StringComparison.Ordinal);
        Assert.Contains(".Label(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("…(truncated)\"", code, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
