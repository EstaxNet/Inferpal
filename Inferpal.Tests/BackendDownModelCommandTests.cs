using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ VS Code refuses a message for the model when the backend is known to be down — no bubble, nothing in the host's
/// history, the text back in the input box — but exempted every slash command, since most are served by the host
/// alone. <c>/explain</c>, <c>/review</c> and a template expanded into a prompt ask the MODEL: sent anyway, they ended
/// on a network error (or a long wait for a remote server) with the text gone from the input box. Visual Studio sends
/// all three through the same pre-flight as a question. These rules read the source (no TypeScript test runner).
/// </summary>
public class BackendDownModelCommandTests
{
    private static string Provider() => WebviewRebuildTests.TsCode("chatViewProvider.ts");

    [Fact]
    public void ExplainAndReview_AreRefusedLikeAQuestion_BeforeTheirBubble()
    {
        var send = WebviewRebuildTests.Body(Provider(), "private async send(");

        Assert.Contains("const asksTheModel = !prompt.startsWith('/') || head === '/explain' || head === '/review';", send,
                        StringComparison.Ordinal);
        var refusal = send.IndexOf("if (asksTheModel && (await this.refusedWhileBackendDown(text)))", StringComparison.Ordinal);
        var bubble  = send.IndexOf("this.append(question);", StringComparison.Ordinal);
        Assert.True(refusal > 0 && bubble > 0, "the refusal or the question bubble moved: the order is not measured");   // WITNESS
        Assert.True(refusal < bubble, "refused after its bubble was shown");
    }

    [Fact]
    public void ATemplateSentAsAPrompt_IsRefusedToo_AndItsBubbleTakenBack()
    {
        var send = WebviewRebuildTests.Body(Provider(), "private async send(");
        var refusal = send.IndexOf("this.refusedWhileBackendDown(text, question)", StringComparison.Ordinal);
        var turn    = send.IndexOf("await this.chatTurn(outcome.chatPrompt, host);", StringComparison.Ordinal);
        Assert.True(refusal > 0 && turn > 0 && refusal < turn, "a template reaches the model without the pre-flight");

        var refuse = WebviewRebuildTests.Body(Provider(), "private async refusedWhileBackendDown(");
        Assert.Contains("this.transcript.splice(at, 1);", refuse, StringComparison.Ordinal);
        Assert.Contains("this.busy = false;", refuse, StringComparison.Ordinal);
        Assert.Contains("this.post({ type: 'setPrompt', text });", refuse, StringComparison.Ordinal);
    }
}
