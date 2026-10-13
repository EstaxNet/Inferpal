using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ VS Code: Regenerate on an <c>/explain</c> or <c>/review</c> answer asks again with what the turn sent — the same
/// code excerpt, as a code action — never an ordinary agent turn.
/// </summary>
/// <remarks>
/// The turn recorded the prompt it sent (the instruction and the excerpt) and Regenerate resent it through
/// <c>send()</c> as if typed: no longer a slash command, it became an ordinary turn — tools, approvals, the chat model
/// instead of the code-actions model —, the whole excerpt became the question's bubble and an entry of the prompt history,
/// and the relevant-code search ran on the code. Visual Studio replays <c>_lastSent</c> (<c>RegenerateResendsWhatWasSentTests</c>).
/// These rules read the source (no TypeScript test runner).
/// </remarks>
public sealed class RegenerateReplaysACodeActionTests
{
    private static string Provider() => WebviewRebuildTests.TsCode("chatViewProvider.ts");

    [Fact]
    public void TheTurnRecords_HowItWasSent()
    {
        var turn = WebviewRebuildTests.Body(Provider(), "private async chatTurn(");
        Assert.Contains("this.lastSent = { question: asked, text: prompt, codeAction, query, shown: asked.text, attachments: [...chips] };",
                        turn, StringComparison.Ordinal);
        // WITNESS: an /explain is still sent as a code action with its instruction as the query.
        Assert.Contains("await this.chatTurn(`${instruction}\\n\\n\\`\\`\\`\\n${excerpt.text}\\n\\`\\`\\``, host, true, instruction);",
                        WebviewRebuildTests.Body(Provider(), "private async runExplainReview("), StringComparison.Ordinal);
    }

    [Fact]
    public void Regenerate_ReplaysIt_BeforeAnyDispatchOnTheWords()
    {
        var regenerate = WebviewRebuildTests.Body(Provider(), "private async regenerate(");
        Assert.Contains("sent && { prompt: sent.text, codeAction: sent.codeAction, query: sent.query }", regenerate, StringComparison.Ordinal);

        var send = WebviewRebuildTests.Body(Provider(), "private async send(");
        var replay = send.IndexOf("await this.chatTurn(replay.prompt, host, replay.codeAction, replay.query);", StringComparison.Ordinal);
        var dispatch = send.IndexOf("const codeAction = ChatViewProvider.codeActionKind(prompt);", StringComparison.Ordinal);
        Assert.True(dispatch > 0, "send() no longer dispatches on the words: the order is not measured.");   // WITNESS
        Assert.True(replay > 0 && replay < dispatch, "a regenerated question is dispatched again from its words");
        // Not typed again: the prompt history keeps what the user wrote.
        Assert.Contains("const history = replay ? this.historyEntries() : this.appendHistory(prompt);", send, StringComparison.Ordinal);
    }
}
