using System.IO;
using System.Text.RegularExpressions;
using Inferpal.Localization;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code offers Regenerate on a reloaded conversation, whose questions kept the names of what went with them, never
/// its content. Resent whole, the bubble's "📎 Attached: X" line reached the model without X. The question now goes again
/// from its words — what they name is read again — and the rest is said once it went. The extension is held on its source,
/// like the rest of its behaviour in this suite.
/// </summary>
public sealed class RegenerateReloadedQuestionTests
{
    private static string Provider() => WebviewRebuildTests.TsCode("chatViewProvider.ts");

    /// <summary>One reader per language, the Core's rule: the LAST recap, nothing after it on its line, a blank line
    /// before it — and usable by both bundles (no editor API, no translation of its own).</summary>
    [Fact]
    public void TheBubbleSplitter_IsTheCoresRule_SharedByTheWebviewAndTheExtension()
    {
        var splitter = WebviewRebuildTests.TsCode("bubbleText.ts");
        Assert.Contains("const at = bubble.lastIndexOf(prefix);", splitter, StringComparison.Ordinal);
        Assert.Contains("if (at < 0 || bubble.indexOf('\\n', at) >= 0) {", splitter, StringComparison.Ordinal);
        Assert.Contains("if (at > 0 && !bubble.slice(0, at).endsWith('\\n\\n')) {", splitter, StringComparison.Ordinal);
        // The Core's names and words, line for line: empty names dropped, none = no recap, trailing blank lines trimmed.
        Assert.Contains("const names = bubble.slice(at + prefix.length).split(' · ').filter((n) => n.length > 0);", splitter, StringComparison.Ordinal);
        Assert.Contains("return names.length === 0 ? { text: bubble, names: [] } : { text: bubble.slice(0, at).replace(/\\n+$/, ''), names };",
                        splitter, StringComparison.Ordinal);
        Assert.DoesNotContain("from 'vscode'", splitter, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\bt\(", splitter);   // no translation of its own: the template is the caller's

        // The whole file: the helper that cuts a body takes the return type's braces for it.
        var webview = WebviewRebuildTests.TsCode("webview/main.ts");
        Assert.Contains("function splitAttachments(text: string): { text: string; names: string[] } {\n  return splitBubbleText(text, attachedRecap);",
                        webview.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("import { splitBubbleText } from './bubbleText';", Provider(), StringComparison.Ordinal);
    }

    /// <summary>The Core's own cases: the rule both splitters follow.</summary>
    [Theory]
    [InlineData("Fix this", "Fix this", 0)]
    [InlineData("Fix this\n\n{R}Foo.cs · Bar.cs", "Fix this", 2)]
    [InlineData("Fix this {R}Foo.cs", "Fix this {R}Foo.cs", 0)]          // quoted mid-text: never cut
    [InlineData("{R}Foo.cs", "", 1)]                                     // only what went with it
    public void TheRule_CutsOnlyARecapOnItsOwnLine(string bubble, string words, int names)
    {
        var prefix = Strings.MsgAttachedRecap("\0").Split('\0')[0];
        var (text, attachments) = Services.Agent.ChatTurnPolicy.SplitBubbleText(bubble.Replace("{R}", prefix));

        Assert.Equal(words.Replace("{R}", prefix), text);
        Assert.Equal(names, attachments.Count);
    }

    [Fact]
    public void AReloadedQuestion_GoesAgainFromItsWords_AndWhatDidNotComeBackIsSaid()
    {
        var regenerate = WebviewRebuildTests.Body(Provider(), "private async regenerate(");
        var split    = regenerate.IndexOf("let reloaded = sent ? undefined : splitBubbleText(question.text, t('📎 Attached: {0}'));", StringComparison.Ordinal);
        var nothing  = regenerate.IndexOf("t('Nothing to ask again:", StringComparison.Ordinal);
        var rollback = regenerate.IndexOf("await host.chatRollbackLastTurn();", StringComparison.Ordinal);
        var resend   = regenerate.IndexOf("await this.send(sent?.shown ?? reloaded?.text ?? question.text, false,", StringComparison.Ordinal);
        var said     = regenerate.IndexOf("const missing = named && reloaded ? reloaded.names.filter((n) => !named.includes(n)) : [];", StringComparison.Ordinal);

        // Split before anything is taken back: a question that was only its attachments is refused with the exchange kept —
        // the refusal RETURNS, or the exchange is rolled back and the empty words are dropped without a word.
        Assert.True(split >= 0 && nothing > split && rollback > nothing, "the split or its refusal comes after the rollback");
        var refusal = regenerate[nothing..rollback].ReplaceLineEndings("\n");
        Assert.Contains("reloaded.names.join(', ')));\n      return;\n    }", refusal, StringComparison.Ordinal);
        // An /explain's two recap lines (its excerpt, then its chips) are both cut.
        Assert.Contains("inner = splitBubbleText(reloaded.text, t('📎 Attached: {0}'))", regenerate, StringComparison.Ordinal);
        // Said after the send, and only for one that reached its turn.
        Assert.True(resend > rollback && said > resend, "what did not come back is not said after the question went");
        var reset = regenerate.IndexOf("this.resendNamed = undefined;", StringComparison.Ordinal);
        Assert.True(reset >= 0 && reset < resend, "what an earlier regeneration named is not cleared before this one leaves");

        // Marked as gone where its request leaves, past a Stop pressed before the host: never at the turn's start.
        var turn = WebviewRebuildTests.Body(Provider(), "private async chatTurn(");
        var stop = turn.IndexOf("if (this.stopPressed) {", StringComparison.Ordinal);
        var went = turn.IndexOf("this.resendNamed = named;", StringComparison.Ordinal);
        var send = turn.IndexOf("await host.chatSend({", StringComparison.Ordinal);
        Assert.True(stop >= 0 && went > stop && send > went, "a regenerated question is marked as gone before its request leaves");

        // A regenerated question is not typed again, from its words or replayed.
        Assert.Contains("const history = replay || this.regenerating ? this.historyEntries() : this.appendHistory(prompt);",
                        Provider(), StringComparison.Ordinal);
    }

    /// <summary>One sentence, two channels: Rewind's (the Core, both editors) and Regenerate's (the extension's bundles).
    /// Both are true in either editor: what the words still name is read again, the rest is attached again by hand.</summary>
    [Fact]
    public void RewindAndRegenerate_SayTheSameSentence()
    {
        // Read in the neutral .resx: the interface language is static, and changing it here would reach other tests.
        var resx = File.ReadAllText(Path.Combine(WebviewRebuildTests.RepoRoot(), "Inferpal.Core", "Localization", "Strings.resx"));
        var english = Regex.Match(resx, @"<data name=""RewindAttachmentsNotAgain"" xml:space=""preserve""><value>(.*?)</value>").Groups[1].Value;
        Assert.NotEmpty(english);

        Assert.Contains("t('" + english.Replace("'", "\\'") + "', missing.join(', '))", Provider(), StringComparison.Ordinal);
    }
}
