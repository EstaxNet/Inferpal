using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Mermaid diagrams drawn in the chat, held on the code (comments left out): what keeps them safe — a page
/// policy that keeps a diagram from reaching the network, Mermaid in strict mode, a diagram shown only as an image —, the
/// code one click away, an open fence left as code, and Mermaid shipped with its licence.
/// </summary>
public class MermaidDiagramTests
{
    private static readonly string Mermaid  = WebviewRebuildTests.TsCode("webview/mermaid.ts").Replace("\r\n", "\n");
    private static readonly string Markdown = WebviewRebuildTests.TsCode("webview/markdown.ts").Replace("\r\n", "\n");
    private static readonly string Html     = WebviewRebuildTests.TsCode("webview/chatWebviewHtml.ts").Replace("\r\n", "\n");

    /// <summary>A diagram's label naming a remote image would reach the network: the chat's policy allows images from
    /// the extension and <c>data:</c> only, and styles from the extension only — never loosened for Mermaid.</summary>
    [Fact]
    public void ThePagePolicy_KeepsADiagramOffTheNetwork()
    {
        Assert.Contains("style-src ${webview.cspSource}; script-src 'nonce-${nonce}'; img-src ${webview.cspSource} data:;", Html, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", Html, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", Html, StringComparison.Ordinal);
    }

    /// <summary>Mermaid in strict mode, its own error panel off; a diagram enters the page as an image (it runs no script and
    /// loads nothing), never as markup; and Mermaid is loaded under the page's nonce, from the extension.</summary>
    [Fact]
    public void ADiagram_IsDrawnStrict_AndShownAsAnImageOnly()
    {
        Assert.Contains("securityLevel: 'strict', suppressErrorRendering: true", Mermaid, StringComparison.Ordinal);
        Assert.Contains("'data:image/svg+xml;base64,' + btoa(binary)", Mermaid, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", Mermaid, StringComparison.Ordinal);
        Assert.Contains("script.nonce = (document.querySelector('script[nonce]')", Mermaid, StringComparison.Ordinal);
        Assert.Contains("'media', 'mermaid', 'mermaid.min.js'", Html, StringComparison.Ordinal);
    }

    /// <summary>Only a closed ```mermaid fence is drawn — an answer still streaming keeps its diagram as code —; one that
    /// does not read says why and shows its code; the code is one click away and copied as written.</summary>
    [Fact]
    public void AnOpenFence_StaysCode_ABrokenOne_SaysWhy_AndTheCodeIsOneClickAway()
    {
        Assert.Contains("token.info.trim().toLowerCase() !== 'mermaid' || !fenceClosed(token.map, renderingLines, token.markup)", Markdown, StringComparison.Ordinal);
        // Closed by the opener's own marker, at least as long, after a blockquote or list prefix.
        Assert.Contains("return run.length >= markup.length && run[0] === markup[0];", Mermaid, StringComparison.Ordinal);
        Assert.Contains(".replace(/^[\\s>]*/, '')", Mermaid, StringComparison.Ordinal);
        Assert.Contains("if (pre.dataset.mermaid === 'closed')", Markdown, StringComparison.Ordinal);
        Assert.Contains("why.textContent = t('mermaidWhy', result.error ?? '');", Mermaid, StringComparison.Ordinal);
        Assert.Contains("pre.hidden = false;", Mermaid, StringComparison.Ordinal);
        Assert.Contains("copyButton.addEventListener('click', () => copy(code));", Mermaid, StringComparison.Ordinal);
    }

    /// <summary>Mermaid (MIT) is shipped with its licence: the build copies both beside the chat page, and nothing in the
    /// packaging rules leaves them out.</summary>
    [Fact]
    public void Mermaid_IsShippedWithItsLicence()
    {
        var root = WebviewRebuildTests.RepoRoot();
        Assert.True(File.Exists(Path.Combine(root, "third-party", "mermaid", "mermaid.min.js")));
        Assert.Contains("MIT License", File.ReadAllText(Path.Combine(root, "third-party", "mermaid", "LICENSE")), StringComparison.Ordinal);
        var build = File.ReadAllText(Path.Combine(root, "vscode", "esbuild.js"));
        Assert.Contains("for (const f of ['mermaid.min.js', 'LICENSE'])", build, StringComparison.Ordinal);
        Assert.DoesNotContain("media/mermaid", File.ReadAllText(Path.Combine(root, "vscode", ".vscodeignore")), StringComparison.Ordinal);
        // Visual Studio's VSIX carries both too, beside the extension (MermaidRenderer loads it from there).
        var vsix = File.ReadAllText(Path.Combine(root, "Inferpal", "Inferpal.csproj"));
        Assert.Contains("<Link>mermaid\\mermaid.min.js</Link>", vsix, StringComparison.Ordinal);
        Assert.Contains("<Link>mermaid\\LICENSE</Link>", vsix, StringComparison.Ordinal);
    }
}
