using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ VS Code decided "the host restarted with a conversation on screen" by <c>transcript.length &gt; 0</c>. At a first start
/// with the backend down — VS Code before Ollama, the ordinary order — the status poll puts its notice in the thread
/// first: that single notice was then saved over <c>last_session</c> (the auto-save shared with Visual Studio) and the
/// restore never ran. The conversation is gone. Only a question or an answer is a conversation; notices are kept under
/// the restored one. These rules read the source.
/// </summary>
public class StartupNoticeKeepsLastSessionTests
{
    private static string Provider() => WebviewRebuildTests.TsCode("chatViewProvider.ts");

    [Fact]
    public void ARestart_IsDecidedByAConversationOnScreen_NotByAnyLine()
    {
        var ready = WebviewRebuildTests.Body(Provider(), "async onHostReady(");
        var poll  = ready.IndexOf("await this.pollBackendStatus();", StringComparison.Ordinal);
        var save  = ready.IndexOf("await host.sessionSave('last_session', this.snapshot());", StringComparison.Ordinal);
        Assert.True(poll >= 0 && save > poll, "the status poll or the rebuild moved: the rule reads nothing");   // WITNESS

        Assert.DoesNotContain("this.transcript.length > 0", ready, StringComparison.Ordinal);
        Assert.DoesNotContain("this.transcript.length === 0", ready, StringComparison.Ordinal);
        Assert.Contains("const conversationShown = this.hasConversation();", ready, StringComparison.Ordinal);
        Assert.Contains("this.transcript.push(...startNotices);", ready, StringComparison.Ordinal);

        var has = WebviewRebuildTests.Body(Provider(), "private hasConversation(");
        Assert.Contains("this.transcript.some((m) => !m.notice)", has, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAutoSave_NeverWritesAThreadOfNoticesOverTheLastConversation()
    {
        var save = WebviewRebuildTests.Body(Provider(), "private autoSaveLast(");
        Assert.Contains("!this.hasConversation()", save, StringComparison.Ordinal);
    }
}
