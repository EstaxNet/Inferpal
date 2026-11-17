using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code's side of an image whose text goes instead of it (pari 108, mockups OcrReady and OcrSent): the banner's two
/// buttons — see the text read, ask the server's model that sees — and the fold under the question with the text sent.
/// </summary>
public sealed class ImageTextFrontEndTests
{
    [Fact]
    public void TheBanner_ShowsTheTextRead_AndHandsTheQuestionToAModelThatSees()
    {
        var chat = WebviewRebuildTests.TsCode("webview/main.ts");
        var provider = WebviewRebuildTests.TsCode("chatViewProvider.ts");

        Assert.Contains("post({ type: 'imageSeeText' })", chat, StringComparison.Ordinal);
        Assert.Contains("post({ type: 'imageAskOther' })", chat, StringComparison.Ordinal);
        Assert.Contains("case 'imageSeeText':", provider, StringComparison.Ordinal);
        Assert.Contains("{ model: this.imageSeen.banner.otherModel, forModel: this.model, agent: this.windowAgentMode() }", provider,
                        StringComparison.Ordinal);
        // Chosen, the model that sees takes the question with the images, once — with the composer's chips (never on
        // Regenerate), and only while the window's model and mode are the ones the banner was offered for.
        var turn = WebviewRebuildTests.Body(provider, "private async chatTurn(");
        Assert.Contains("const askOther = this.regenerating ? undefined : this.imageAskOther;", turn, StringComparison.Ordinal);
        Assert.Matches(@"askOther\.forModel === this\.model && askOther\.agent === this\.windowAgentMode\(\)\) \{\s*model = askOther\.model;\s*modelOverridesRoles = true;", turn);
        // The last image removed takes the choice with it.
        Assert.Contains("if (!this.pendingAttachments.some((a) => a.image)) {\n            this.imageAskOther = undefined;",
                        provider.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTextSent_IsFoldedUnderTheQuestion_AsTheModelGotIt()
    {
        var chat = WebviewRebuildTests.TsCode("webview/main.ts");
        var turn = WebviewRebuildTests.Body(WebviewRebuildTests.TsCode("chatViewProvider.ts"), "private async chatTurn(");

        Assert.Contains("imageTexts.push({ label: text.label, text: text.forModel });", turn, StringComparison.Ordinal);
        Assert.Contains("asked.imageTexts = imageTexts;", turn, StringComparison.Ordinal);
        Assert.Contains("for (const read of item.imageTexts ?? [])", chat, StringComparison.Ordinal);
        Assert.Contains("fold.open = true;", chat, StringComparison.Ordinal);
    }
}
