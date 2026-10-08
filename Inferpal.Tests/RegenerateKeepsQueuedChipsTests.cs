using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code's Regenerate resends the last question with what IT was sent with — its words and its chips — and leaves the
/// chips waiting in the composer for the next question.
/// </summary>
/// <remarks>
/// ⚠ Two halves of one rule. Resent through the ordinary send, it took every chip waiting in the composer: chips attached
/// for the NEXT question went to the model with the old one. And resent from its bubble's words alone, "explain this
/// file" went without the file — the bubble keeps only the "📎 Attached" line naming it. Visual Studio resends
/// <c>_lastSent</c> (<c>RegenerateResendsWhatWasSentTests</c>). These rules read the source.
/// </remarks>
public class RegenerateKeepsQueuedChipsTests
{
    private static string Provider() => WebviewRebuildTests.TsCode("chatViewProvider.ts");

    [Fact]
    public void Regenerate_LeavesTheComposersChips_ForTheNextQuestion()
    {
        var regenerate = WebviewRebuildTests.Body(Provider(), "private async regenerate(");
        var flag = regenerate.IndexOf("this.regenerating = true;", StringComparison.Ordinal);
        var send = regenerate.IndexOf("await this.send(", StringComparison.Ordinal);
        Assert.True(send > 0, "Regenerate no longer resends: the rule reads nothing");   // WITNESS
        Assert.True(flag >= 0 && flag < send, "Regenerate takes the chips queued for the next question");

        var turn = WebviewRebuildTests.Body(Provider(), "private async chatTurn(");
        Assert.Contains("const chips = this.regenerating ? this.resendAttachments ?? [] : this.pendingAttachments;", turn, StringComparison.Ordinal);
        Assert.Contains("if (!this.regenerating) {", turn, StringComparison.Ordinal);
    }

    [Fact]
    public void Regenerate_ResendsTheQuestionsOwnWordsAndChips()
    {
        var regenerate = WebviewRebuildTests.Body(Provider(), "private async regenerate(");
        Assert.Contains("this.lastSent?.question === question", regenerate, StringComparison.Ordinal);   // tied to its bubble
        Assert.Contains("this.resendAttachments = sent?.attachments;", regenerate, StringComparison.Ordinal);
        Assert.Contains("await this.send(sent?.text ?? question.text);", regenerate, StringComparison.Ordinal);

        // What is resent is recorded where the turn consumes it: the words before the "📎 Attached" line, the chips sent.
        var turn = WebviewRebuildTests.Body(Provider(), "private async chatTurn(");
        var record = turn.IndexOf("this.lastSent = { question: asked, text: prompt, attachments: [...chips] };", StringComparison.Ordinal);
        var naming = turn.IndexOf("this.nameAttachmentsInQuestion(named);", StringComparison.Ordinal);
        Assert.True(record > 0 && naming > record, "the question's words are recorded after the chips' names were added to it");
    }
}
