using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ VS Code's /explain and /review read the editor AFTER their question is shown. Refused there (no file open, the
/// excerpt failed), the question was left unmarked though the host never received it — and Regenerate, which takes back
/// the host's LAST question, then removed the exchange before it: the host forgot a question and answer the screen still
/// showed. The question is a notice until the turn reaches the host. These rules read the source.
/// </summary>
public class ExplainRefusedQuestionTests
{
    private static string Provider() => WebviewRebuildTests.TsCode("chatViewProvider.ts");

    [Fact]
    public void TheQuestion_IsANotice_UntilTheTurnReachesTheHost()
    {
        var send   = WebviewRebuildTests.Body(Provider(), "private async send(");
        var branch = send[send.IndexOf("if (first === '/explain' || first === '/review') {", StringComparison.Ordinal)..];
        var mark   = branch.IndexOf("question.notice = true;", StringComparison.Ordinal);
        var run    = branch.IndexOf("await this.runExplainReview(", StringComparison.Ordinal);
        Assert.True(run > 0, "the /explain branch moved: the rule reads nothing");   // WITNESS
        Assert.True(mark >= 0 && mark < run, "a refused /explain leaves a question the host never received");

        var review  = WebviewRebuildTests.Body(Provider(), "private async runExplainReview(");
        var refused = review.IndexOf("t('Open a file in the editor to use /{0}.', kind)", StringComparison.Ordinal);
        var clear   = review.IndexOf("question.notice = false;", StringComparison.Ordinal);
        var excerpt = review.IndexOf("await host.codeExcerpt(", StringComparison.Ordinal);
        var turn    = review.IndexOf("await this.chatTurn(", StringComparison.Ordinal);
        Assert.True(refused >= 0 && excerpt > refused, "the refusal or the excerpt moved: the rule reads nothing");   // WITNESS
        Assert.True(clear > excerpt && clear < turn, "the question is cleared before the turn really starts");
    }
}
