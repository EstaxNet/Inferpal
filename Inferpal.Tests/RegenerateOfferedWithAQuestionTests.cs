using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code: Regenerate is offered only when the thread holds a question meant for the model, and with no host it
/// names the remedy.
/// </summary>
/// <remarks>
/// The button went under the newest assistant bubble, whatever it was: under a thread of notices alone — the "backend
/// unreachable" line at start, a slash command's output — it was offered, and the click returned in silence (no question
/// to ask again). With the host stopped it returned in silence too, through a compound guard the gesture rule did not
/// read (<see cref="SilentGestureTests"/>, widened with this fix).
/// </remarks>
public sealed class RegenerateOfferedWithAQuestionTests
{
    private static string Src(params string[] parts) =>
        Path.Combine([ConversationPersistenceSilenceTests.RepoRoot(), "vscode", "src", .. parts]);

    private static string Code(params string[] parts) =>
        SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(Src(parts)));

    [Fact]
    public void TheExtension_SaysWhetherThereIsAQuestion_WithEveryRedrawOfTheButton()
    {
        var src = Code("chatViewProvider.ts");

        var predicate = ConversationPersistenceSilenceTests.TsMethodBody(Src("chatViewProvider.ts"), "private canRegenerate(");
        Assert.Contains("ChatViewProvider.isModelQuestion(m)", predicate, StringComparison.Ordinal);   // the one reader

        // Every message after which the webview redraws the button carries the answer — a property of the posts,
        // not a list of sites: a fourth "turnEnded" without it would leave the button as the previous turn left it.
        var ended = Regex.Matches(src, @"type: 'turnEnded',(?<body>[^}]*)\}", RegexOptions.None, TimeSpan.FromSeconds(5));
        Assert.True(ended.Count >= 3, $"Only {ended.Count} turnEnded post(s) read: the scan no longer sees them.");
        foreach (Match m in ended)
            Assert.Contains("canRegenerate: this.canRegenerate()", m.Groups["body"].Value, StringComparison.Ordinal);

        var hydrate = ConversationPersistenceSilenceTests.TsMethodBody(Src("chatViewProvider.ts"), "private hydrate(");
        Assert.Contains("canRegenerate: this.canRegenerate()", hydrate, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWebview_OffersTheButton_OnlyWhenTold()
    {
        var offer = ConversationPersistenceSilenceTests.TsMethodBody(Src("webview", "main.ts"), "function refreshRegenerate(");
        Assert.Contains("post({ type: 'regenerate' })", offer, StringComparison.Ordinal);   // witness
        Assert.Contains("!canRegenerate", offer, StringComparison.Ordinal);

        var src = Code("webview", "main.ts");
        Assert.Equal(2, Regex.Matches(src, Regex.Escape("canRegenerate = msg.canRegenerate === true"),
                                      RegexOptions.None, TimeSpan.FromSeconds(5)).Count);   // hydrate and turnEnded
    }

    [Fact]
    public void Regenerate_WithNoHost_GoesThroughTheGestureFunnel()
    {
        var body = ConversationPersistenceSilenceTests.TsMethodBody(Src("chatViewProvider.ts"), "private async regenerate(");

        Assert.Contains("this.hostForGesture()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("this.getHost()", body, StringComparison.Ordinal);
    }
}
