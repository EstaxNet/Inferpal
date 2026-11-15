using System;
using System.Linq;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The proxy access block of VS Code is the Core's rule read again in TypeScript (<c>settingsProxy.ts</c>): the same
/// header names, the same docs page, drawn over the one field that saves them. The extension is held on its source,
/// like the rest of its behaviour in this suite.
/// </summary>
public sealed class ProxyFrontEndTests
{
    private static string Proxy() => WebviewRebuildTests.TsCode("webview/settingsProxy.ts");

    [Fact]
    public void VsCodesProxyBlock_WritesTheCoresHeaders_AndOpensTheCoresPage()
    {
        var code = Proxy();
        Assert.Contains($"const CF_ID = '{ProxyAccess.CfId}';", code, StringComparison.Ordinal);
        Assert.Contains($"const CF_SECRET = '{ProxyAccess.CfSecret}';", code, StringComparison.Ordinal);
        Assert.Contains($"export const REVERSE_PROXY_PAGE = '{Services.DocsLinks.ReverseProxyPage}';", code, StringComparison.Ordinal);
        // The Core's writing, line for line: Basic over UTF-8, Bearer trimmed, the Cloudflare pair on two lines.
        Assert.Contains("'Authorization: Basic ' + toBase64(form.user.trim() + ':' + form.password)", code, StringComparison.Ordinal);
        Assert.Contains("'Authorization: Bearer ' + form.token.trim()", code, StringComparison.Ordinal);
        Assert.Contains("`${CF_ID}: ${form.clientId.trim()}\\n${CF_SECRET}: ${form.clientSecret.trim()}`", code, StringComparison.Ordinal);
        Assert.DoesNotContain("from 'vscode'", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProxyBlock_IsDrawnOverTheRequestHeadersField_InBothEditors()
    {
        var field = Services.Presentation.SettingsSchema.Tabs.SelectMany(t => t.Sections).SelectMany(s => s.Fields)
                                                         .Single(f => f.Key == "requestHeaders");
        Assert.Equal("proxyAccess", field.Editor);
        Assert.Contains("case 'proxyAccess':   return proxyAccessEditor(area, host);",
                        WebviewRebuildTests.TsCode("webview/settingsEditors.ts"), StringComparison.Ordinal);
        // The Test sends the form's headers and says the hops; a save says whether the old proxy's go with a new address.
        var panel = WebviewRebuildTests.TsCode("webview/settings.ts");
        Assert.Contains("requestHeaders,", panel, StringComparison.Ordinal);
        Assert.Contains("drawHops(msg.hops ?? null);", panel, StringComparison.Ordinal);
        Assert.Contains("keepRequestHeaders });", panel, StringComparison.Ordinal);
    }
}
