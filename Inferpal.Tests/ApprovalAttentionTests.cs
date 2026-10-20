using System.IO;
using System.Text.RegularExpressions;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An approval card left unanswered says so outside the conversation after <see cref="ApprovalCard.AttentionDelay"/>, to a
/// window without the focus only, and never after its answer: Visual Studio flashes its taskbar button, VS Code shows a
/// notification for as long as the card waits.
/// </summary>
/// <remarks>
/// ⚠ A run started before its author goes elsewhere stops at its first card: without the signal, nothing outside the
/// panel says so. Neither editor's card runs headless: the sources are read, without their comments.
/// </remarks>
public sealed class ApprovalAttentionTests
{
    private static string Root => ConventionCoverageTests.RepoRoot();

    [Fact]
    public void BothEditors_WaitTheSameTime()
    {
        var provider = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(Root, "vscode", "src", "chatViewProvider.ts")));
        var ms = Regex.Match(provider, @"const APPROVAL_ATTENTION_MS = ([\d_]+);");
        Assert.True(ms.Success, "APPROVAL_ATTENTION_MS not found in chatViewProvider.ts");   // WITNESS
        Assert.Equal(ApprovalCard.AttentionDelay.TotalMilliseconds, double.Parse(ms.Groups[1].Value.Replace("_", "")));
    }

    [Fact]
    public void VisualStudio_FlashesAfterTheDelay_UnlessAnsweredFirst_AndNeverAWindowInFront()
    {
        var chrome = ConventionCoverageTests.CodeOnly(Path.Combine(Root, "Inferpal", "ToolWindow", "InferpalToolWindowData.Chrome.cs"));
        // Started once the card is in the conversation, with the token its answer cancels.
        var ask = chrome[chrome.IndexOf("private async Task<ApprovalDecision?> AskInlineAsync", StringComparison.Ordinal)..];
        ask = ask[..ask.IndexOf("private ", 10, StringComparison.Ordinal)];
        Assert.True(ask.IndexOf("_ = DrawAttentionAfterAsync(answeredToken);", StringComparison.Ordinal)
                    > ask.IndexOf("if (item is null) return null;", StringComparison.Ordinal),
                    "the attention is not started after the card is shown");
        Assert.Contains("await answered.CancelAsync();", ask, StringComparison.Ordinal);
        // In front at that moment, it is asked again until the window is left; flashed, it is stopped at the answer.
        Assert.Contains("== Services.VsIntegration.WindowAttention.Outcome.Looking)", chrome, StringComparison.Ordinal);
        Assert.Contains("await Task.Delay(TimeSpan.FromSeconds(2), answered);", chrome, StringComparison.Ordinal);
        Assert.Contains("if (flashed) Services.VsIntegration.WindowAttention.Stop();", chrome, StringComparison.Ordinal);

        var attention = ConventionCoverageTests.CodeOnly(Path.Combine(Root, "Inferpal", "Services", "VsIntegration", "WindowAttention.cs"));
        // Any window of this Visual Studio in front is "looking" — a floating tool window too, not only the main one.
        Assert.Contains("GetWindowThreadProcessId(GetForegroundWindow(), out var foreground);", attention, StringComparison.Ordinal);
        Assert.Contains("foreground == (uint)devenv) return Outcome.Looking;", attention, StringComparison.Ordinal);
        Assert.Contains("FlashTray | FlashUntilSeen", attention, StringComparison.Ordinal);
        Assert.Contains("Flash(window, FlashStop);", attention, StringComparison.Ordinal);
        Assert.Contains("Signals.SignalScope.VsInstanceKey", attention, StringComparison.Ordinal);   // the devenv resolved once
    }

    [Fact]
    public void VsCode_NotifiesAWindowWithoutTheFocus_ForAsLongAsTheCardWaits()
    {
        var provider = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(Root, "vscode", "src", "chatViewProvider.ts")));
        string Between(string start, string end)
        {
            var from = provider.IndexOf(start, StringComparison.Ordinal);
            Assert.True(from >= 0, start);
            var rest = provider[from..];
            return rest[..rest.IndexOf(end, StringComparison.Ordinal)];
        }
        var helper = Between("private static attentionWhileWaiting(", "async requestInput(");

        Assert.Contains("}, APPROVAL_ATTENTION_MS);", helper, StringComparison.Ordinal);
        Assert.Contains("if (notified || !waiting()) {", helper, StringComparison.Ordinal);
        Assert.Contains("if (!vscode.window.state.focused) {", helper, StringComparison.Ordinal);
        // Focused at that moment, it waits for the window to lose the focus.
        Assert.Contains("watch = vscode.window.onDidChangeWindowState((state) => {", helper, StringComparison.Ordinal);
        // A progress notification tied to the card's answer: it closes with the answer, never left behind.
        Assert.Contains("withProgress({ location: vscode.ProgressLocation.Notification, title }, () => answer);", helper, StringComparison.Ordinal);
        Assert.Contains("return { dispose: () => { clearTimeout(timer); watch?.dispose(); } };", helper, StringComparison.Ordinal);

        // Both cards a turn waits on: the approval under its sentence, the server's form under its own title, each while
        // it is pending, and each stopped in its finally.
        foreach (var (card, call) in new[]
        {
            (Between("async requestApproval(", "private static attentionWhileWaiting("),
             "attentionWhileWaiting(\n      t('Inferpal is waiting for your approval.'), () => this.pendingApprovals.has(id), answer);"),
            (Between("async requestInput(", "private dismissInput("),
             "attentionWhileWaiting(card.title, () => this.pendingInputs.has(card.id), answer);"),
        })
        {
            Assert.Contains(call, card.Replace("\r\n", "\n"), StringComparison.Ordinal);
            Assert.Contains("attention.dispose();", card[card.IndexOf("finally", StringComparison.Ordinal)..], StringComparison.Ordinal);
        }
    }
}
