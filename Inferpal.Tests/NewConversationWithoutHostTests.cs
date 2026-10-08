using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code: "New conversation" with the host stopped keeps the conversation on screen and names the host's remedy.
/// </summary>
/// <remarks>
/// It used to clear the screen anyway: the conversation was not archived (archiving needs the host), and when the host
/// came back, <c>onHostReady</c> found no conversation on screen and reloaded <c>last_session</c> — the conversation the
/// user had just left, back as if nothing had been asked. A thread of notices only is still cleared: there is nothing
/// to archive, and nothing a restart would bring back that the user meant to drop.
/// </remarks>
public sealed class NewConversationWithoutHostTests
{
    private static string Provider() =>
        Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "vscode", "src", "chatViewProvider.ts");

    [Fact]
    public void ANewConversation_WithNoHost_KeepsTheConversation_AndSaysTheRemedy()
    {
        var body = ConversationPersistenceSilenceTests.TsMethodBody(Provider(), "async resetConversation(");

        var clear = body.IndexOf("this.transcript.length = 0", StringComparison.Ordinal);
        Assert.True(clear >= 0, "the reset no longer clears the transcript: this test would have measured nothing.");

        var guard = body.IndexOf("!host?.isRunning && this.hasConversation()", StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < clear, "with no host, the reset clears a conversation it cannot archive.");
        var branch = body[guard..clear];
        Assert.Contains("this.hostForGesture()", branch, StringComparison.Ordinal);
        Assert.Contains("return;", branch, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRestart_ReloadsTheLastSession_WhenNoConversationIsShown()
    {
        // The reason the guard exists: the restore that would undo a blank screen. If it goes, the guard is worth
        // re-reading rather than keeping by habit.
        var body = ConversationPersistenceSilenceTests.TsMethodBody(Provider(), "async onHostReady(");

        Assert.Contains("if (!conversationShown)", body, StringComparison.Ordinal);
        Assert.Contains("host.sessionLoad('last_session')", body, StringComparison.Ordinal);
    }
}
