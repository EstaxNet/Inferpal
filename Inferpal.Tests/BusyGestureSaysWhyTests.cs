using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code: a gesture that sends while a turn runs by another door than the composer — the editor's context menu
/// (Explain, Fix, Refactor, Doc), the result bar's Undo — says why nothing happens.
/// </summary>
/// <remarks>
/// These gestures reach <c>send()</c> by another door, and it returned on <c>busy</c> without a word: the chat took the
/// focus and the click did nothing at all. Visual Studio says it (<c>Strings.UndoRunWhileBusy</c>), and queues a
/// context-menu action, whose code it takes at the click; VS Code's reads the editor when it runs, so queued it would
/// rewrite whatever is selected by then — refused instead. The composer itself sends a note or queues
/// (<see cref="SteeringFrontEndTests"/>).
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
        Assert.Contains("post({ type: 'send', text: `/undo-run #${runId}` })", main, StringComparison.Ordinal);   // the result bar's Undo
        Assert.Contains("case 'send':", provider, StringComparison.Ordinal);
    }
}
