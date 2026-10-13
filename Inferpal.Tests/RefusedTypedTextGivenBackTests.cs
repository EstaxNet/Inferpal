using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ VS Code: a typed message refused before its turn goes back in the input box — and only a typed one does.
/// </summary>
/// <remarks>
/// The webview clears the composer as it sends. With no host running (it is restarting, or it crashed) the provider
/// said "the host is not running" and the message was gone: the box was empty, nothing kept it. The backend-down refusal
/// gave its text back, but to every gesture — a welcome card, the result bar's Undo, Regenerate put their own text over
/// the draft the user had in the box, and the message claimed "your message is back in the input box". The composer
/// marks its send `typed`; both refusals give back exactly that, and say so only then. These rules read the source (no
/// TypeScript test runner).
/// </remarks>
public sealed class RefusedTypedTextGivenBackTests
{
    private static string Provider() => WebviewRebuildTests.TsCode("chatViewProvider.ts");

    [Fact]
    public void WithNoHost_ATypedMessage_GoesBackToTheInputBox()
    {
        var send = WebviewRebuildTests.Body(Provider(), "private async send(");
        var noHost = send.IndexOf("if (!host?.isRunning) {", StringComparison.Ordinal);
        Assert.True(noHost > 0, "send() no longer refuses without a host: the rule measures nothing.");   // WITNESS
        var branch = send[noHost..send.IndexOf("return;", noHost, StringComparison.Ordinal)];
        Assert.Contains("this.giveBackTyped(text, typed);", branch, StringComparison.Ordinal);
        // Said only when it is true.
        Assert.Contains("typed ? `${hostUnavailableMessage()} ${t('Your message was not sent and is back in the input box.')}` : hostUnavailableMessage()",
                        branch, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheComposersText_IsGivenBack()
    {
        var give = WebviewRebuildTests.Body(Provider(), "private giveBackTyped(");
        Assert.Contains("if (typed) {", give, StringComparison.Ordinal);
        Assert.Contains("this.post({ type: 'setPrompt', text });", give, StringComparison.Ordinal);
        // No refusal posts the text straight: it would land over a draft.
        Assert.Equal(1, Count(Provider(), "this.post({ type: 'setPrompt', text });"));

        // The composer marks its send; the provider reads the mark; a card or a button does not set it.
        var webview = WebviewRebuildTests.TsCode("webview/main.ts");
        Assert.Contains("post({ type: 'send', text, typed: true });", WebviewRebuildTests.Body(webview, "function send("), StringComparison.Ordinal);
        Assert.Equal(1, Count(webview, "typed: true"));
        Assert.Contains("await this.send(msg.text, msg.typed === true);", Provider(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheBackendDownRefusal_SaysTheBoxHoldsTheText_OnlyWhenItDoes()
    {
        var refuse = WebviewRebuildTests.Body(Provider(), "private async refusedWhileBackendDown(");
        Assert.Contains("t('The backend is unreachable — nothing was sent.')", refuse, StringComparison.Ordinal);
        Assert.Contains("t('The backend refused the connection check — nothing was sent. Check the API key and the URL in the settings.')",
                        refuse, StringComparison.Ordinal);
        // Each "is back in the input box" sits under `typed ?`.
        foreach (var said in new[] { "t('The backend is unreachable — your message was not sent and is back in the input box.')",
                                     "t('The backend refused the connection check — your message was not sent and is back in the input box." })
        {
            var at = refuse.IndexOf(said, StringComparison.Ordinal);
            Assert.True(at > 0, $"{said} no longer said: the rule measures nothing.");   // WITNESS
            Assert.EndsWith("typed\n          ? ", refuse[..at].Replace("\r", "", StringComparison.Ordinal), StringComparison.Ordinal);
        }
    }

    private static int Count(string text, string what)
    {
        var n = 0;
        for (var at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal))
            n++;
        return n;
    }
}
