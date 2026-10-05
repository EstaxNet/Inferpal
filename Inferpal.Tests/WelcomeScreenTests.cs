using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The welcome screen offers only gestures that work in the state it is shown in.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Without an open file, both editors offered <c>/explain</c>, <c>/fix</c> and <c>/test</c> — three commands that
/// answer "open a file" in exactly that state: the cards were shown only when they could not work. The no-file cards
/// now ask about the project and the uncommitted changes, and a line says what an open file adds.
/// </para>
/// <para>
/// ⚠ And VS Code's "Fix them" sent <c>@problems Fix these errors.</c> as text: nothing expands a typed
/// <c>@problems</c> (it is a picker entry, not a file), so the model received the request without a single error.
/// The banner now attaches the Problems panel as the picker does, then sends.
/// </para>
/// These rules read the sources: the webview has no test runner, and the window's view model needs Visual Studio.
/// </remarks>
public class WelcomeScreenTests
{
    private static readonly string[] FileCommands = ["/explain", "/fix", "/test"];

    [Fact]
    public void WithoutAFile_VsCodeOffersNoCardThatNeedsOne()
    {
        // WITNESS: those commands do refuse without a file.
        Assert.Contains("Open a file in the editor to use /{0}.", WebviewRebuildTests.TsCode("chatViewProvider.ts"), StringComparison.Ordinal);

        var welcome = WebviewRebuildTests.Body(WebviewRebuildTests.TsCode("webview/main.ts"), "function buildWelcome(");
        var noFile  = welcome[welcome.IndexOf("} else {", StringComparison.Ordinal)..welcome.IndexOf("welcomeEl.appendChild(cards)", StringComparison.Ordinal)];

        Assert.Contains("welcomeProjectPrompt", noFile, StringComparison.Ordinal);
        foreach (var command in FileCommands)
            Assert.DoesNotContain($"text: '{command}'", noFile, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAFile_VisualStudioOffersNoCardThatNeedsOne()
    {
        // WITNESS: those commands do refuse without a file.
        var slash = File.ReadAllText(Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal", "ToolWindow",
                                                  "InferpalToolWindowData.SlashCommands.cs"));
        Assert.Contains("Strings.SlashNoActiveDocument", slash, StringComparison.Ordinal);

        var xaml   = File.ReadAllText(Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal", "ToolWindow",
                                                   "InferpalToolWindowContent.xaml"));
        var start  = xaml.IndexOf("<!-- Without one: the general entry points -->", StringComparison.Ordinal);
        Assert.True(start >= 0, "the no-file welcome grid moved: the rule reads nothing");
        var noFile = xaml[start..xaml.IndexOf("</UniformGrid>", start, StringComparison.Ordinal)];

        Assert.Contains("WelcomeProjectPrompt", noFile, StringComparison.Ordinal);
        foreach (var command in FileCommands)
            Assert.DoesNotContain($"CommandParameter=\"{command}\"", noFile, StringComparison.Ordinal);
    }

    [Fact]
    public void FixThem_AttachesTheProblems_InsteadOfTypingTheirMention()
    {
        var webview = WebviewRebuildTests.TsCode("webview/main.ts");
        Assert.DoesNotContain("'@problems '", webview, StringComparison.Ordinal);
        Assert.Contains("type: 'fixProblems'", WebviewRebuildTests.Body(webview, "function buildWelcome("), StringComparison.Ordinal);

        var provider = WebviewRebuildTests.TsCode("chatViewProvider.ts");
        var at       = provider.IndexOf("case 'fixProblems':", StringComparison.Ordinal);
        Assert.True(at >= 0, "the provider does not answer the banner's message");
        var handler  = provider[at..provider.IndexOf("case '", at + 10, StringComparison.Ordinal)];
        var attach   = handler.IndexOf("this.resolveMention('problems')", StringComparison.Ordinal);
        Assert.True(attach >= 0 && attach < handler.IndexOf("this.send(", StringComparison.Ordinal),
            "the errors are not attached before the request is sent");
    }

    // The banner announces "N error(s)" and its button attaches the report: both read the panel through one reader, or
    // an error in settings.json or an untitled buffer is counted by one and dropped by the other — "1 error", then
    // "No problems in the Problems panel".
    [Fact]
    public void TheBannerCount_AndTheReportFixThemAttaches_ReadThePanelTheSameWay()
    {
        var bridge = WebviewRebuildTests.TsCode("editorBridge.ts");
        Assert.Matches(@"getDiagnostics\(\)\.filter\(\(\[uri\]\)\s*=>\s*uri\.scheme\s*===\s*'file'\)",
                       WebviewRebuildTests.Body(bridge, "export function panelDiagnostics("));
        Assert.Contains("panelDiagnostics()", WebviewRebuildTests.Body(bridge, "async editorDiagnostics("), StringComparison.Ordinal);

        var provider = WebviewRebuildTests.TsCode("chatViewProvider.ts");
        Assert.Contains("panelDiagnostics()", WebviewRebuildTests.Body(provider, "private editorContext(): { editorFile: string | null; problems: number }"), StringComparison.Ordinal);

        // No third reader: the panel is read in exactly one place.
        foreach (var file in new[] { "chatViewProvider.ts", "editorBridge.ts", "extension.ts" })
            Assert.Equal(file == "editorBridge.ts" ? 1 : 0,
                         System.Text.RegularExpressions.Regex.Matches(WebviewRebuildTests.TsCode(file), @"languages\.getDiagnostics\(").Count);
    }
}
