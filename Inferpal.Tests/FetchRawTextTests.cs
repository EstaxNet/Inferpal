using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// fetch_url ran every response through the HTML-to-text conversion, plain text included — and a raw source file is
/// one of the most common things an agent fetches (raw.githubusercontent.com). Anything between '&lt;' and '&gt;' was
/// stripped as a tag and every indent trimmed: <c>List&lt;string&gt;</c> came back <c>List</c>, and
/// <c>if (a &lt; b &amp;&amp; c &gt; d)</c> came back <c>if (a  d)</c>.
/// </summary>
public class FetchRawTextTests
{
    private const string Source =
        "public sealed class Registry\n{\n    public List<string> Names { get; } = [];\n\n" +
        "    public bool Check(int a, int b, int c, int d)\n    {\n        return a < b && c > d;\n    }\n}\n";

    [Fact]
    public void TheHtmlConversion_MangledSourceCode()
    {
        // The measurement this class exists for: what every body went through.
        var text = FetchUrlTool.HtmlToText(Source);

        Assert.DoesNotContain("List<string>", text);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("text/x-csharp")]
    [InlineData("application/json")]
    [InlineData("text/markdown")]
    public void APlainTextResponse_ComesBackAsWritten(string mediaType)
    {
        var text = FetchUrlTool.Readable(new WebPage.WebText(Source, mediaType));

        Assert.Contains("    public List<string> Names { get; } = [];", text);
        Assert.Contains("        return a < b && c > d;", text);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/xhtml+xml")]
    [InlineData(null)]   // no header: the body says what it is
    public void AnHtmlPage_IsStillConverted(string? mediaType)
    {
        // Reference arm: a page stays a page — its tags go, its text stays.
        var text = FetchUrlTool.Readable(new WebPage.WebText(
            "<!DOCTYPE html><html><body><p>Hello <b>world</b></p></body></html>", mediaType));

        Assert.Equal("Hello world", text);
    }

    [Fact]
    public void TheTool_ReadsEveryResponseThroughReadable()
    {
        // fetch_url refuses a loopback address by design, so the suite cannot drive it: this keeps its one reading on
        // the function tested above.
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var code = ConventionCoverageTests.CodeOnly(
            System.IO.Path.Combine(dir!.FullName, "Inferpal.Core", "Services", "Tools", "FetchUrlTool.cs"));

        Assert.Contains("Window(Readable(", code);
        Assert.DoesNotContain("Window(HtmlToText(", code);
    }
}
