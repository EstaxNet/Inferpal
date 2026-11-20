using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code's side of a run on a copy (<c>/run-copy</c>, pari 106): a question asked on the copy keeps the command as its
/// bubble and is never resent as an ordinary question — Regenerate, or a reloaded conversation, asks it again through the
/// start card, never in the person's files; the card waiting for the person survives a redraw.
/// </summary>
/// <remarks>These rules read the source: what must not come back is a Regenerate that writes in the person's files a
/// question they asked to keep away from them, and only a run with a model reaches it otherwise.</remarks>
public class RunCopyFrontEndTests
{
    private static string Provider()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        // Line endings folded: a Windows checkout (CI) has this file in CRLF, and the cuts below end on "\n  }\n".
        return SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(dir!.FullName, "vscode", "src", "chatViewProvider.ts"))).Replace("\r\n", "\n");
    }

    [Fact]
    public void AQuestionAskedOnTheCopy_IsNeverKeptForAnOrdinaryRegenerate()
    {
        var code = Provider();

        // The send kept for Regenerate is skipped for a copy question: its bubble (`/run-copy …`) asks again through the card.
        Assert.Matches(new Regex(@"if \(asked && !inCopy\) \{\s*this\.lastSent = "), code);
        Assert.Matches(new Regex(@"this\.send\(`/run-copy \$\{question\}`, false, \{[^}]*inCopy: true"), code);
        Assert.Contains("inCopy: inCopy || undefined", code);
    }

    [Fact]
    public void ContinueAfterARunOnTheCopy_ContinuesInTheCopy()
    {
        var code = Provider();
        var body = code[code.IndexOf("private async continueTurn(): Promise<void> {", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("\n  }\n", StringComparison.Ordinal)];

        Assert.Contains("ChatViewProvider.isCopyQuestion(last.text)", body);
        Assert.Matches(new Regex(@"this\.send\(inCopy \? `/run-copy \$\{prompt\}` : prompt, false,\s*\{[^}]*inCopy \}"), body);
    }

    [Fact]
    public void TheChangesThePersonDropped_SurviveARedraw_AndACardOfAStoppedHostGoes()
    {
        var code = Provider();
        var main = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(Path.Combine(
            Path.GetDirectoryName(ProviderPath())!, "webview", "main.ts"))).Replace("\r\n", "\n");
        var ready = code[code.IndexOf("private async onHostReady(): Promise<void> {", StringComparison.Ordinal)..];
        ready = ready[..ready.IndexOf("host.setChatEvents(", StringComparison.Ordinal)];

        Assert.Contains("this.copyCard.dropped = msg.dropped;", code);                               // the extension keeps them
        Assert.Contains("renderCopyReview(el, card.review, card.dropped ?? [])", main);               // a redraw draws them
        Assert.Contains("f.texts?.hunkKeys[n]", main);                                               // by what they are
        Assert.Contains("post({ type: 'copyChoice', dropped: now })", main);
        Assert.Contains("this.showCopyCard(null);", ready);
    }

    private static string ProviderPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "vscode", "src", "chatViewProvider.ts");
    }

    [Fact]
    public void RegenerateOnACopyQuestion_TakesNothingBack_AndContinueWithoutACopyGoesThroughTheCard()
    {
        var code = Provider();
        var regenerate = code[code.IndexOf("private async regenerate(): Promise<void> {", StringComparison.Ordinal)..];
        var rollback = regenerate.IndexOf("chatRollbackLastTurn", StringComparison.Ordinal);
        var copy = regenerate.IndexOf("ChatViewProvider.isCopyQuestion(question.text)", StringComparison.Ordinal);
        var cont = code[code.IndexOf("private async continueTurn(): Promise<void> {", StringComparison.Ordinal)..];

        Assert.True(copy > 0 && copy < rollback, "a copy question is taken back before its card asks it again");
        Assert.Matches(new Regex(@"!\(await host\.copyPrepare\(\)\)\.held\)\s*\{\s*await this\.openCopyStart\(prompt\);"), cont);
    }

    [Fact]
    public void TheNoticesCopyLeftBehind_IsReviewedOrDiscarded_ThenSaidNoMore()
    {
        var code = Provider();
        var act = code[code.IndexOf("private async actOnLeftCopy(", StringComparison.Ordinal)..];
        act = act[..act.IndexOf("\n  }\n", StringComparison.Ordinal)];

        Assert.Contains("const taken = await host.copyTakeUp(folder);", act);                       // exactly the named copy
        Assert.Matches(new Regex(@"if \(!taken\.held\) \{\s*this\.sayOutOfTurn\(taken\.error"), act);   // refused: said
        Assert.Contains("await this.discardCopy();", act);
        Assert.DoesNotContain("sessionAwayDismissed", act);                                          // taken up, it is held
        Assert.Contains("await host.sessionAwayDismissed(msg.runIds, msg.taskIds, msg.copyFolder);", code);
    }

    [Fact]
    public void TheCardWaitingForThePerson_IsDrawnAgainAfterEveryRedraw()
    {
        var code = Provider();
        var hydrate = code[code.IndexOf("private hydrate(): void {", StringComparison.Ordinal)..];
        hydrate = hydrate[..hydrate.IndexOf("\n  }\n", StringComparison.Ordinal)];

        Assert.Contains("this.post({ type: 'copyCard', card: this.copyCard })", hydrate);
    }
}
