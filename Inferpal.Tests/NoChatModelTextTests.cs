using System.IO;
using Inferpal.Localization;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A server that answers and lists models, none of which can chat (embedding, reranking), is said as such on every
/// screen: the empty model menu said "is the backend reachable?", Suggest said "only embedding models" (a reranker is
/// listed too), the ACP setup said "no model installed" — three remedies for a problem the person does not have.
/// </summary>
public class NoChatModelTextTests
{
    [Fact]
    public void VisualStudio_SaysNoChatModel_WhenModelsAreListedButNoneCanChat()
    {
        var chrome = ConventionCoverageTests.CodeOnly(
            Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.Chrome.cs"));
        Assert.Contains("listed.Listed.Count > 0 ? Strings.NoChatModelListed : Strings.ChatNoModelListed", chrome,
                        StringComparison.Ordinal);
    }

    [Fact]
    public void VsCode_SaysNoChatModel_WhenModelsAreListedButNoneCanChat()
    {
        var provider = WebviewRebuildTests.TsCode("chatViewProvider.ts");
        Assert.Contains("byKind.chat.length === 0 && (byKind.listed?.length ?? 0) > 0", provider, StringComparison.Ordinal);
        Assert.Contains("onlyOtherModels: this.onlyOtherModels", provider, StringComparison.Ordinal);

        var menu = WebviewRebuildTests.Body(WebviewRebuildTests.TsCode("webview/main.ts"), "function renderModelMenu(");
        Assert.Contains("t(onlyOtherModels ? 'noChatModelListed' : 'noModelListed')", menu, StringComparison.Ordinal);

        // The webview's string table carries the Core's English sentence, the key its translations are found under.
        Assert.Contains("noChatModelListed: t('" + English + "')", WebviewRebuildTests.TsCode("webview/chatWebviewHtml.ts"),
                        StringComparison.Ordinal);
    }

    private const string English =
        "The server lists no chat model — only models that cannot answer (embedding, reranking). Install a chat model.";

    [Fact]
    public void TheCoresEnglishSentence_IsTheOneVsCodeTranslates()
    {
        var resx = File.ReadAllText(Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal.Core", "Localization", "Strings.resx"));
        Assert.Contains($"<data name=\"{nameof(Strings.NoChatModelListed)}\" xml:space=\"preserve\"><value>{English}</value></data>",
                        resx, StringComparison.Ordinal);
    }
}
