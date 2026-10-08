using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code: with the host not running, the model badge and its "Retry" name the HOST's remedy, never "backend unreachable".
/// </summary>
/// <remarks>
/// With no folder open the host is never started — the state of every fresh install, VS Code opening on its Welcome tab.
/// The badge then showed a red dot and "Backend unreachable", and Retry silently re-posted the same thing: the user went
/// to check an Ollama or LM Studio server that was running fine, with a button that did nothing. The backend was not
/// asked at all; the badge now says so in the words <c>hostUnavailableMessage</c> already uses everywhere else.
/// </remarks>
public sealed class HostDownBadgeTests
{
    private static string VsCodeSrc(string file) =>
        Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "vscode", "src", file);

    [Fact]
    public void TheStatusPoll_WithNoHost_SaysTheHostIsDown()
    {
        var body = ConversationPersistenceSilenceTests.TsMethodBody(VsCodeSrc("chatViewProvider.ts"), "private async pollBackendStatus(");

        Assert.Contains("host.backendStatus()", body, StringComparison.Ordinal);   // witness: the poll itself
        Assert.Contains("hostDown: hostUnavailableMessage()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Retry_WithNoHost_IsAGestureThatNamesTheRemedy()
    {
        var src   = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(VsCodeSrc("chatViewProvider.ts")));
        var start = src.IndexOf("case 'retryConnection':", StringComparison.Ordinal);
        Assert.True(start >= 0, "the retry case was not found: this test would have measured nothing.");
        var branch = src[start..src.IndexOf("return;", start, StringComparison.Ordinal)];

        Assert.Contains("this.hostForGesture()", branch, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBadge_ReadsTheHostsSentence_BeforeUnreachable()
    {
        var body = ConversationPersistenceSilenceTests.TsMethodBody(VsCodeSrc(Path.Combine("webview", "main.ts")), "function renderModelButton(");

        var hostDown    = body.IndexOf("status.hostDown", StringComparison.Ordinal);
        var unreachable = body.IndexOf("t('statusUnreachable')", StringComparison.Ordinal);
        Assert.True(unreachable >= 0, "the unreachable text is no longer read here: check what replaced it.");
        Assert.True(hostDown >= 0 && hostDown < unreachable, "the badge does not prefer the host's sentence.");
    }
}
