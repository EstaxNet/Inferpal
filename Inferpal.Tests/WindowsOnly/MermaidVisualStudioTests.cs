using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio's Mermaid diagrams: only a closed fence is drawn, and the render page keeps a diagram off the network.
/// Windows only: the Visual Studio adapter's types.
/// </summary>
public class MermaidVisualStudioTests
{
    /// <summary>Visual Studio draws a ```mermaid fence only once it is closed: an answer stopped inside it keeps it as code,
    /// and a fence of another language is never a diagram.</summary>
    [Theory]
    [InlineData("```mermaid\nflowchart TD\n  A --> B\n```\n", true)]
    [InlineData("```Mermaid title\nflowchart TD\n  A --> B\n```\n", true)]
    [InlineData("```mermaid\nflowchart TD\n  A --> B\n", false)]
    [InlineData("```js\nconst a = 1;\n```\n", false)]
    public void VisualStudio_DrawsAClosedMermaidFenceOnly(string answer, bool diagram)
    {
        var block = Services.Presentation.MarkdownParser.Parse(answer).Single(b => b.Type == "code_block");
        var shown = Inferpal.ToolWindow.MarkdownBlock.FromModel(block);
        Assert.Equal(diagram, shown.IsMermaid);
        Assert.Equal(diagram, shown.Diagram is not null);
    }

    /// <summary>Visual Studio's render page (a WebView2): its policy lets nothing a diagram names reach the network, and
    /// Mermaid runs strict.</summary>
    [Fact]
    public void VisualStudiosRenderPage_KeepsADiagramOffTheNetwork()
    {
        var page = Services.VsIntegration.MermaidRenderer.RenderPage("file:///x/mermaid.min.js");
        Assert.Contains("content=\"default-src 'none'; script-src file: 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; font-src data:\"", page, StringComparison.Ordinal);
        Assert.Contains("securityLevel: 'strict', suppressErrorRendering: true", page, StringComparison.Ordinal);
        // Driven through WebView2's own channel, its DevTools window off: never a debugging port, which any account of the
        // machine could attach to.
        var renderer = ConventionCoverageTests.CodeOnly(Path.Combine(WebviewRebuildTests.RepoRoot(), "Inferpal", "Services", "VsIntegration", "MermaidRenderer.cs"));
        Assert.Contains(".CallDevToolsProtocolMethodAsync(method, json)", renderer, StringComparison.Ordinal);
        Assert.Contains("web.Settings.AreDevToolsEnabled = false;", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("remote-debugging", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("AdditionalBrowserArguments", renderer, StringComparison.Ordinal);
    }
}
