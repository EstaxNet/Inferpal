using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code: a gesture that sends while a turn runs — the editor's context menu (Explain, Fix, Refactor, Doc), the result
/// bar's Undo — says why nothing happens.
/// </summary>
/// <remarks>
/// The composer cannot send during a turn, but these gestures reach <c>send()</c> by another door, and it returned on
/// <c>busy</c> without a word: the chat took the focus and the click did nothing at all. Visual Studio says it
/// (<c>Strings.UndoRunWhileBusy</c>; Explain stops the running turn first).
/// </remarks>
public sealed class BusyGestureSaysWhyTests
{
    private static string Src(string file) =>
        Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "vscode", "src", file);

    [Fact]
    public void SendDuringATurn_SaysSo()
    {
        var send = ConversationPersistenceSilenceTests.TsMethodBody(Src("chatViewProvider.ts"), "private async send(");

        var busy = send.IndexOf("if (this.busy)", StringComparison.Ordinal);
        Assert.True(busy >= 0, "send() no longer checks for a running turn: check what replaced it.");
        var branch = send[busy..send.IndexOf("return;", busy, StringComparison.Ordinal)];
        Assert.Contains("showWarningMessage(t('A turn is still running", branch, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGesturesThatBypassTheComposer_ReachSend()
    {
        // Witnesses: the doors this guard exists for still lead here.
        var provider = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(Src("chatViewProvider.ts")));
        Assert.Contains("await this.send(text);", ConversationPersistenceSilenceTests.TsMethodBody(Src("chatViewProvider.ts"), "async runSlashCommand("),
                        StringComparison.Ordinal);
        var main = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(Src(Path.Combine("webview", "main.ts"))));
        Assert.Contains("'/undo-run'", main, StringComparison.Ordinal);
        Assert.Contains("case 'send':", provider, StringComparison.Ordinal);
    }
}
