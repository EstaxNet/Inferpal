using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ VS Code's Regenerate takes the last exchange back from the host before asking again — and the host removes its
/// LAST question. It decided by the identity of "the last question sent to the model", which no reload, load or branch
/// recomputed, plus "does not start with a slash": a reloaded <c>/explain</c> or template was resent ON TOP of its own
/// restored exchange (the model read it twice). A question that never reached the host's history — a command served
/// without the model, a request the host refused — is marked a notice (the mark a reload keeps), and only an unmarked
/// one is taken back. ⚠ A turn that FAILED is not one of them: the host keeps its question (as Visual Studio does), so
/// marked, Regenerate resent it on top of itself. A template's question is its expanded prompt, as in Visual Studio.
/// These rules read the source.
/// </summary>
public class RegenerateRollbackTests
{
    private static string Provider() => WebviewRebuildTests.TsCode("chatViewProvider.ts");

    [Fact]
    public void Regenerate_TakesBack_OnlyAQuestionTheHostKept()
    {
        var provider = Provider();
        Assert.DoesNotContain("lastModelQuestion", provider, StringComparison.Ordinal);

        var regenerate = WebviewRebuildTests.Body(provider, "private async regenerate(");
        var mark     = regenerate.IndexOf("if (!question.notice) {", StringComparison.Ordinal);
        var rollback = regenerate.IndexOf("await host.chatRollbackLastTurn();", StringComparison.Ordinal);
        Assert.True(rollback > 0, "the rollback moved: the rule measures nothing");   // WITNESS
        Assert.True(mark > 0 && mark < rollback, "the rollback is not decided by the mark");
    }

    // The host adds the question to its history before anything can fail, and its failure paths keep it: an error it
    // ANSWERS leaves the question a question. Only a request it REFUSED (thrown, never reached the turn) is a notice.
    [Fact]
    public void AnAnsweredError_KeepsItsQuestion_ARefusedRequestMarksIt()
    {
        var turn     = WebviewRebuildTests.Body(Provider(), "private async chatTurn(");
        var answered = turn.IndexOf("this.append({ role: 'error', text: result.error", StringComparison.Ordinal);
        var refused  = turn.IndexOf("} catch (err) {", StringComparison.Ordinal);
        Assert.True(answered > 0 && refused > answered, "the two failure branches moved: the rule measures nothing");   // WITNESS

        var marks = System.Text.RegularExpressions.Regex.Matches(turn, @"asked\.notice = true;");
        Assert.Single(marks);
        Assert.True(marks[0].Index > refused, "a failed turn's question is marked, though the host keeps it");

        // The host side of the contract: a failed run restores the durable history, which already holds the question.
        var host  = File.ReadAllText(Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal.Host", "HostServer.cs"));
        var added = host.IndexOf("s.History.Add(new ChatMessageDto(\"user\", promptText));", StringComparison.Ordinal);
        var copy  = host.IndexOf("var durable = new List<ChatMessageDto>(s.History);", StringComparison.Ordinal);
        Assert.True(added > 0 && copy > added, "the durable history no longer holds the question it restores on failure");
    }

    [Fact]
    public void ATemplatesQuestion_IsItsExpandedPrompt()
    {
        var send = WebviewRebuildTests.Body(Provider(), "private async send(");
        var text = send.IndexOf("question.text = outcome.chatPrompt;", StringComparison.Ordinal);
        var turn = send.IndexOf("await this.chatTurn(outcome.chatPrompt, host);", StringComparison.Ordinal);
        Assert.True(text > 0 && turn > text, "a template is still shown and saved under its slash name");
    }
}
