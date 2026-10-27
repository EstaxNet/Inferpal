using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>@terminal</c> in VS Code: the last command a terminal finished — followed from its START, since its output is
/// readable only from there; kept by its END, where the verdict is, the cut said; served by the editor, which sees the
/// terminal, never by the host; not offered by Visual Studio, which cannot serve it yet. These rules read the sources: the
/// extension has no TypeScript test runner (its pure module was run once under Node: its cleaning and its cut).
/// </summary>
public class TerminalMentionTests
{
    [Fact]
    public void ACommandIsFollowedFromItsStart_AndKeptByItsEnd()
    {
        var history = WebviewRebuildTests.TsCode("terminalHistory.ts");
        var follow  = WebviewRebuildTests.Body(history, "private async follow(");

        // Read as the command starts: read when @terminal is typed, the output would be empty. And only in a terminal
        // @terminal was asked in: from read(), VS Code keeps a running command's whole output in memory.
        Assert.Matches(@"onDidStartTerminalShellExecution\(\(e\) => \{\s*if \(this\.followed\.has\(e\.terminal\)\) \{\s*void this\.follow\(e\);", history);
        Assert.Contains("const stream = e.execution.read()[Symbol.asyncIterator]();", follow, StringComparison.Ordinal);
        Assert.Contains("this.followed.add(terminal);", WebviewRebuildTests.Body(history, "| { notice: string }"), StringComparison.Ordinal);
        // A terminal closed mid-command ends its reading.
        Assert.Contains("await Promise.race([stream.next(), closed])", follow, StringComparison.Ordinal);
        Assert.DoesNotContain(".read()", WebviewRebuildTests.Body(history, "| { notice: string }"), StringComparison.Ordinal);
        // Kept by its end, the cut counted and said.
        Assert.Contains("new TailBuffer(KEPT)", follow, StringComparison.Ordinal);
        Assert.Contains("cut: buffer.dropped() > 0", follow, StringComparison.Ordinal);
        var text = WebviewRebuildTests.TsCode("terminalText.ts");
        Assert.DoesNotContain("from 'vscode'", text, StringComparison.Ordinal);   // pure, as attachmentCap.ts
        Assert.Contains("the start of the output is cut", text, StringComparison.Ordinal);
        Assert.Contains("this.text.slice(over)", text, StringComparison.Ordinal);   // the END is kept
    }

    [Fact]
    public void NothingToAttach_SaysWhy()
    {
        var attachment = WebviewRebuildTests.Body(WebviewRebuildTests.TsCode("terminalHistory.ts"), "| { notice: string }");

        foreach (var why in new[]
                 {
                     "t('No terminal is open.')", "shellIntegrationOff()",
                     "t(\"Inferpal follows this terminal's commands from now on:", "t('A command is still running in this terminal:",
                     "t('No command has finished in this terminal; the last one finished in “{0}”:",
                     "t('No command has finished in this terminal since Inferpal began following it:",
                 })
            Assert.Contains(why, attachment, StringComparison.Ordinal);
        var provider = WebviewRebuildTests.Body(WebviewRebuildTests.TsCode("chatViewProvider.ts"), "private async resolveMention(");
        Assert.Contains("void vscode.window.showInformationMessage(terminal.notice);", provider, StringComparison.Ordinal);
    }

    [Fact]
    public void VisualStudioDoesNotOfferIt()
    {
        var terminal = Assert.Single(MentionController.Categories, c => c.Token == "@terminal");
        Assert.Equal(MentionEditors.VsCode, terminal.Editors);
        Assert.False(terminal.QueryBased);
        Assert.DoesNotContain(MentionController.MatchCategories("ter"), c => c.Token == "@terminal");
    }
}
