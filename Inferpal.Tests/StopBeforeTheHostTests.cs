using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code: a Stop pressed after the turn started but before <c>chat/send</c> left ends the turn, nothing sent.
/// </summary>
/// <remarks>
/// <c>/explain</c> sizes its excerpt from the host first, a typed <c>@file</c> is resolved, a template is expanded: the
/// Stop button is showing, and <c>chat/cancel</c> reaches a host with no turn to cancel. The request then went anyway,
/// the answer streaming after the user had stopped it. The provider keeps the press and reads it last, right before
/// the request.
/// </remarks>
public sealed class StopBeforeTheHostTests
{
    private static string Provider() =>
        Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "vscode", "src", "chatViewProvider.ts");

    [Fact]
    public void AStopPressedBeforeTheRequest_EndsTheTurn_WithNothingSent()
    {
        var body = ConversationPersistenceSilenceTests.TsMethodBody(Provider(), "private async chatTurn(");
        var send = body.IndexOf("await host.chatSend(", StringComparison.Ordinal);
        Assert.True(send >= 0, "the turn's request was not found: this test would have measured nothing.");

        var check = body.LastIndexOf("if (this.stopPressed)", send, StringComparison.Ordinal);
        Assert.True(check >= 0, "nothing reads a Stop pressed before the request: the turn is sent anyway.");
        Assert.Contains("this.endStoppedBeforeTheHost()", body[check..send], StringComparison.Ordinal);
        // Read LAST: a Stop pressed during anything awaited after the check would be lost again.
        Assert.DoesNotContain("await ", body[check..send], StringComparison.Ordinal);
    }

    [Fact]
    public void ThePress_IsKeptOnlyDuringATurn_AndForgottenWhenTheNextOneStarts()
    {
        var src = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(Provider()));
        var cancel = src.IndexOf("case 'cancel':", StringComparison.Ordinal);
        Assert.True(cancel >= 0, "the Stop case was not found: this test would have measured nothing.");
        var hostCancel = src.IndexOf("chatCancel()", cancel, StringComparison.Ordinal);
        Assert.True(hostCancel > cancel, "Stop no longer asks the host to cancel: check what replaced it.");
        var kept = src[cancel..hostCancel];
        Assert.Contains("if (this.busy)", kept, StringComparison.Ordinal);
        Assert.Contains("this.stopPressed = true", kept, StringComparison.Ordinal);

        // A Stop from an earlier turn does not stop the next one.
        var start = src.IndexOf("this.busy = true;", StringComparison.Ordinal);
        Assert.True(start >= 0, "the turn's start was not found.");
        Assert.Equal(start, src.LastIndexOf("this.busy = true;", StringComparison.Ordinal));
        Assert.Contains("this.stopPressed = false;", src.Substring(start, 80), StringComparison.Ordinal);
    }

    [Fact]
    public void TheUnsentQuestion_IsANotice_AndTheTurnEndsCancelled()
    {
        var body = ConversationPersistenceSilenceTests.TsMethodBody(Provider(), "private endStoppedBeforeTheHost(");
        Assert.Contains("asked.notice = true", body, StringComparison.Ordinal);
        Assert.Contains("t('Cancelled.')", body, StringComparison.Ordinal);
        Assert.Contains("cancelled: true", body, StringComparison.Ordinal);
        Assert.Contains("this.busy = false", body, StringComparison.Ordinal);
    }
}
