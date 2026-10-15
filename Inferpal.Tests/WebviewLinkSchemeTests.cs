using System.IO;
using System.Text.RegularExpressions;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code's chat keeps a link the model wrote clickable only for a web address, as Visual Studio's does.
/// </summary>
/// <remarks>
/// markdown-it's own check refuses <c>javascript:</c>, <c>vbscript:</c>, <c>file:</c> and <c>data:</c> only: a
/// <c>vscode://…</c> link, <c>ms-settings:</c>, or any scheme a program registered stayed clickable in the webview, and
/// its click launched that program — the model writes these links. Visual Studio opens web addresses only
/// (<c>MarkdownParser.WebUrl</c>).
/// </remarks>
public sealed class WebviewLinkSchemeTests
{
    private static string Markdown() => SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(
        Path.Combine(ConventionCoverageTests.RepoRoot(), "vscode", "src", "webview", "markdown.ts")));

    [Fact]
    public void EveryRenderedLink_IsDisarmedUnlessItIsAWebAddress()
    {
        var ts = Markdown();
        Assert.Contains(@"return /^https?:\/\/[^\s]/i.test(url.trim());", ts, StringComparison.Ordinal);
        // Applied to whatever the renderer produced, right after it: markdown-it's links, its autolinks, linkify's.
        Assert.Matches(new Regex(@"target\.innerHTML = [^;]+;\s*disarmLinks\(target\);"), ts);
        Assert.Contains("if (!isWebLink(a.getAttribute('href') ?? ''))", ts, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.com/docs", true)]
    [InlineData("http://example.com", true)]
    [InlineData("vscode://vscode.git/clone?url=x", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("docs/models.md", false)]
    public void TheTwoEditors_AgreeOnWhatIsAWebAddress(string url, bool web)
    {
        // The same answer as the webview's predicate above, read by Visual Studio's renderer.
        Assert.Equal(web, MarkdownParser.WebUrl(url) is not null);
        Assert.Equal(web, Regex.IsMatch(url.Trim(), @"^https?://[^\s]", RegexOptions.IgnoreCase));
    }
}
