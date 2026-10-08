using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code: a question asked while the host starts waits for the restore of the last conversation, then goes.
/// </summary>
/// <remarks>
/// The start brings <c>last_session</c> back only when the thread shows no conversation. A question asked in the seconds
/// it takes (model list, configuration, backend check) became the conversation on screen: it was saved over
/// <c>last_session</c> and the restore never ran — yesterday's conversation gone. Asked before the host even existed,
/// the "host not running" line it got did the same, since that line counted as a conversation. Visual Studio restores in
/// its constructor and queues context-menu actions behind it.
/// </remarks>
public sealed class StartupQuestionWaitsForRestoreTests
{
    private static string Src(string file) =>
        Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "vscode", "src", file);

    private static string Code(string file) =>
        SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(Src(file)));

    [Fact]
    public void AQuestionAskedDuringTheStart_IsQueued_BeforeAnyHostCheck()
    {
        var send = ConversationPersistenceSilenceTests.TsMethodBody(Src("chatViewProvider.ts"), "private async send(");

        var starting = send.IndexOf("if (this.hostStarting)", StringComparison.Ordinal);
        var noHost   = send.IndexOf("if (!host?.isRunning)", StringComparison.Ordinal);
        Assert.True(noHost > 0, "the host check of send() was not found: this test would have measured nothing.");
        Assert.True(starting > 0 && starting < noHost, "a question sent while the host starts reaches the host checks.");
        var branch = send[starting..noHost];
        Assert.Contains("this.queuedPrompts.push(prompt)", branch, StringComparison.Ordinal);
        Assert.Contains("return;", branch, StringComparison.Ordinal);
    }

    [Fact]
    public void TheQueue_IsReleasedOnlyOnceTheRestoreIsDone_OrTheStartFailed()
    {
        var started = ConversationPersistenceSilenceTests.TsMethodBody(Src("chatViewProvider.ts"), "async onHostStarted(");
        var restore = started.IndexOf("await this.onHostReady()", StringComparison.Ordinal);
        var release = started.IndexOf("this.releaseQueuedPrompts()", StringComparison.Ordinal);
        Assert.True(restore >= 0, "onHostStarted no longer runs the restore: check what replaced it.");
        Assert.True(release > restore && started.IndexOf("finally", restore, StringComparison.Ordinal) is var f && f > restore && f < release,
                    "the queue is not released in a finally after the restore.");

        // The restore itself still reloads last_session when nothing is on screen — the reason the queue exists.
        var ready = ConversationPersistenceSilenceTests.TsMethodBody(Src("chatViewProvider.ts"), "async onHostReady(");
        Assert.Contains("host.sessionLoad('last_session')", ready, StringComparison.Ordinal);

        var ext = Code("extension.ts");
        var starting = ext.IndexOf("chatView.onHostStarting()", StringComparison.Ordinal);
        var spawn    = ext.IndexOf("await client.start()", StringComparison.Ordinal);
        Assert.True(starting > 0 && starting < spawn, "the activator does not mark the start before spawning the host.");
        Assert.Contains("await chatView.onHostStarted()", ext, StringComparison.Ordinal);
        Assert.Contains("chatView.onHostStartFailed()", ext[spawn..], StringComparison.Ordinal);
    }

    [Fact]
    public void TheHostNotRunningLine_IsANotice_NotAConversation()
    {
        var send   = ConversationPersistenceSilenceTests.TsMethodBody(Src("chatViewProvider.ts"), "private async send(");
        var noHost = send.IndexOf("if (!host?.isRunning)", StringComparison.Ordinal);
        Assert.True(noHost > 0, "the host check of send() was not found.");
        var branch = send[noHost..send.IndexOf("return;", noHost, StringComparison.Ordinal)];

        Assert.Contains("text: hostUnavailableMessage()", branch, StringComparison.Ordinal);   // witness
        Assert.Contains("notice: true", branch, StringComparison.Ordinal);
    }
}
