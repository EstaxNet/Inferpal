using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ A link in Visual Studio's chat keeps its address and opens it. Shown as its text alone, "docs/models.md" in the
/// message that tells a fresh install where to find a model led nowhere — the address was dropped, and an
/// <c>&lt;https://…&gt;</c> autolink was dropped whole. Web addresses only: a model writes these links, and the system's
/// application for another scheme runs a program.
/// </summary>
[Collection(CultureSerialCollection.Name)]
public sealed class ChatLinkTests
{
    private static IReadOnlyList<InlineRunModel> Runs(string markdown) =>
        [.. MarkdownParser.Parse(markdown).SelectMany(b => b.Inlines)];

    [Fact]
    public void ALinksWords_CarryItsAddress_AnAutolinkIsKept()
    {
        var runs = Runs("See [the models page](https://example.com/models) first.");
        Assert.Equal("https://example.com/models", Assert.Single(runs, r => r.Text == "the models page").Url);
        Assert.Null(Assert.Single(runs, r => r.Text.Contains("See")).Url);   // reference arm: the text around is not a link

        var auto = Assert.Single(Runs("Docs: <https://example.com/docs>"), r => r.Url is not null);
        Assert.Equal("https://example.com/docs", auto.Text);   // its text IS the address — no longer dropped
        Assert.Equal("https://example.com/docs", auto.Url);
    }

    [Fact]
    public void OnlyAWebAddress_BecomesALink()
    {
        foreach (var markdown in new[]
                 {
                     "[run](file:///C:/Windows/System32/calc.exe)", "[x](javascript:alert(1))", "[x](ms-settings:display)",
                     "[relative](docs/models.md)", "[anchor](#by-graphics-card)", "![image](https://example.com/a.png)",
                 })
            Assert.All(Runs(markdown), r => Assert.Null(r.Url));
    }

    /// <summary>Every word of a link opens it: Visual Studio lays a paragraph out one word per child.</summary>
    [Fact]
    public void EveryWordOfALink_OpensIt_AndTheWordsAroundItDoNot()
    {
        var model = Assert.Single(MarkdownParser.Parse("Read [the full guide](https://example.com/g) today"));
        var pieces = InlineFlow.Pieces(model.Inlines);
        var linkWords = pieces.Where(p => p.Text.Trim() is "the" or "full" or "guide").ToList();
        var others    = pieces.Where(p => p.Text.Trim() is "Read" or "today").ToList();
        Assert.Equal(3, linkWords.Count);   // WITNESS: one piece per word — joined with the text around, none would be read
        Assert.Equal(2, others.Count);
        Assert.All(linkWords, p => Assert.Equal("https://example.com/g", p.Url));
        Assert.All(others, p => Assert.Null(p.Url));
    }

    /// <summary>The product's own message with a documentation link: followable in Visual Studio, in every language.</summary>
    [Fact]
    public void TheNoModelMessage_LinksTheModelsPage()
    {
        foreach (var language in new[] { "en", "fr", "ja" })
        {
            Strings.ApplyLanguage(language);
            try
            {
                Assert.Contains(Runs(Strings.MsgFirstRunNoModels("Ollama", canPull: false)),
                                r => r.Url == "https://github.com/EstaxNet/Inferpal/blob/master/docs/models.md#by-graphics-card");
            }
            finally { Strings.ApplyLanguage(null); }
        }
    }

    /// <summary>Visual Studio's chat: a link's words are underlined, show their address and open it — through the one
    /// opener, which refuses anything but a web address.</summary>
    [Fact]
    public void VisualStudiosChat_OpensALinkOnClick_ThroughTheWebOpener()
    {
        var root = ConventionCoverageTests.RepoRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalToolWindowContent.xaml"));
        Assert.Contains("<MouseBinding MouseAction=\"LeftClick\" Command=\"{Binding OpenCommand}\"/>", xaml, StringComparison.Ordinal);
        Assert.Matches(@"<DataTrigger Binding=""\{Binding IsLink\}"" Value=""True"">\s*<Setter Property=""TextDecorations"" Value=""Underline""/>\s*<Setter Property=""Cursor""\s+Value=""Hand""/>\s*<Setter Property=""ToolTip""\s+Value=""\{Binding Url\}""/>", xaml);

        string Code(string file) => ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal", "ToolWindow", file));
        Assert.Contains("OpenCommand = piece.Url is { } url ? new AsyncCommand((_, _) => { WebLink.Open(url); return Task.CompletedTask; }) : null",
                        Code("MarkdownBlock.cs"), StringComparison.Ordinal);
        var opener = Code("WebLink.cs");
        Assert.True(opener.IndexOf("MarkdownParser.WebUrl(url)", StringComparison.Ordinal)
                    < opener.IndexOf("Process.Start", StringComparison.Ordinal), "the opener starts a process before checking the address");
    }
}
