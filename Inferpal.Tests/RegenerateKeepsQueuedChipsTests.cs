using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ VS Code's Regenerate re-sends the last question through the ordinary send — which takes every chip waiting in
/// the composer. Chips attached for the NEXT question left the composer and went to the model with the old one. Visual
/// Studio resends with no attachment (<c>SendCoreAsync(userText, …, attachments: [], …)</c>). These rules read the source.
/// </summary>
public class RegenerateKeepsQueuedChipsTests
{
    private static string Provider() => WebviewRebuildTests.TsCode("chatViewProvider.ts");

    [Fact]
    public void Regenerate_LeavesTheComposersChips_ForTheNextQuestion()
    {
        var regenerate = WebviewRebuildTests.Body(Provider(), "private async regenerate(");
        var flag = regenerate.IndexOf("this.regenerating = true;", StringComparison.Ordinal);
        var send = regenerate.IndexOf("await this.send(question.text);", StringComparison.Ordinal);
        Assert.True(send > 0, "Regenerate no longer resends: the rule reads nothing");   // WITNESS
        Assert.True(flag >= 0 && flag < send, "Regenerate takes the chips queued for the next question");

        var turn = WebviewRebuildTests.Body(Provider(), "private async chatTurn(");
        Assert.Contains("if (this.pendingAttachments.length > 0 && !this.regenerating) {", turn, StringComparison.Ordinal);
    }
}
