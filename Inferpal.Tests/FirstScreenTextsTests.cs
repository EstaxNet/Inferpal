using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The first screen says what the product does. The mode tooltips tell Chat (tools, step by step) from Agent (a plan
/// first) — the Chat mode HAS the tools, and a tooltip that says otherwise puts a newcomer's first decision on a false
/// text. While an approval card waits, the empty composer names what its keys do — Enter allows once, Esc denies: the
/// turn's hint ("Enter: a note for the running turn") is what Enter does only once something is typed.
/// </summary>
/// <remarks>Neither editor's chrome runs headless: the sources are read, without their comments; the English texts are
/// read from the neutral resource file, never through the process-wide culture.</remarks>
public sealed class FirstScreenTextsTests
{
    private static string Root => ConventionCoverageTests.RepoRoot();

    private static string English(string key)
    {
        var resx = File.ReadAllText(Path.Combine(Root, "Inferpal.Core", "Localization", "Strings.resx"));
        var m = Regex.Match(resx, "<data name=\"" + Regex.Escape(key) + "\" xml:space=\"preserve\"><value>(.*?)</value>",
                            RegexOptions.Singleline);
        Assert.True(m.Success, $"{key} is not in Strings.resx");   // WITNESS
        return m.Groups[1].Value;
    }

    /// <summary>The English text the webview declares under <paramref name="name"/> (<c>name: t('…')</c>).</summary>
    private static string Webview(string name)
    {
        var html = WebviewRebuildTests.TsCode("webview/chatWebviewHtml.ts");
        var m = Regex.Match(html, Regex.Escape(name) + @": t\('((?:[^'\\]|\\.)*)'\)");
        Assert.True(m.Success, $"{name} is not declared in chatWebviewHtml.ts");   // WITNESS
        return m.Groups[1].Value;
    }

    [Fact]
    public void TheChatTip_SaysItHasTheTools_AndTheAgentTip_NamesThePlan()
    {
        Assert.Equal("Answers with tools, step by step; asks before changing anything", English("ModeChatTip"));
        Assert.Equal("Plans the task, then works through it; asks before changing anything", English("ModeAgentTip"));
    }

    /// <summary>The composer shows its hint only when EMPTY, and there, with a card waiting, Enter allows once and Esc
    /// denies in both editors: the hint names those keys — "Enter: refuse the card" would have made a refusal approve.</summary>
    [Fact]
    public void TheCardHint_NamesWhatTheKeysDo_InTheEmptyComposer() =>
        Assert.StartsWith("Enter: allow once · Esc: deny", English("ComposerPlaceholderCard"), StringComparison.Ordinal);

    [Theory]
    [InlineData("ModeChatTip", "modeChatTip")]
    [InlineData("ModeAgentTip", "modeAgentTip")]
    [InlineData("ModePlanTip", "modePlanTip")]
    [InlineData("ComposerPlaceholderBusy", "composerPlaceholderBusy")]
    [InlineData("ComposerPlaceholderCard", "composerPlaceholderCard")]
    public void BothEditors_ShowTheSameEnglishText(string resxKey, string webviewName) =>
        Assert.Equal(English(resxKey), Webview(webviewName));

    private static int Find(string text, string anchor, int from = 0)
    {
        var at = text.IndexOf(anchor, from, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{anchor}' not found: this rule would measure nothing");   // WITNESS
        return at;
    }

    [Fact]
    public void VisualStudio_SaysWhatTheKeysDo_WhileACardWaits()
    {
        var chrome = ConventionCoverageTests.CodeOnly(Path.Combine(Root, "Inferpal", "ToolWindow", "InferpalToolWindowData.Chrome.cs"));
        // One reader of what the keys do now: the card first, then the running turn.
        var hint = chrome[Find(chrome, "private void RefreshComposerPlaceholder()")..];
        Assert.Contains("_pendingApproval is { IsAnswered: false } ? Strings.ComposerPlaceholderCard", hint, StringComparison.Ordinal);
        Assert.Contains("!IsLoading ? Strings.ComposerPlaceholder", hint, StringComparison.Ordinal);

        // Redrawn when the card shows (before the wait for its answer), and again once it is gone.
        var ask = chrome[Find(chrome, "private async Task<ApprovalDecision?> AskInlineAsync")..];
        ask = ask[..Find(ask, "private void InsertCard")];
        var shown = Find(ask, "_pendingApproval = created;");
        Assert.True(Find(ask, "RefreshComposerPlaceholder();", shown) < Find(ask, "finally"), "not redrawn when the card shows");
        Assert.True(Find(ask, "RefreshComposerPlaceholder();", Find(ask, "_pendingApproval = null;")) > 0, "not redrawn when the card goes");

        // Every writer of the placeholder is that reader: a second one would show the turn's hint over a waiting card.
        var vm = string.Join("\n", ConventionCoverageTests.ViewModelSources().Select(ConventionCoverageTests.CodeOnly));
        Assert.Single(Regex.Matches(vm, @"\bComposerPlaceholder\s*=(?!=)"));
    }

    [Fact]
    public void VsCode_SaysWhatTheKeysDo_WhileOneCardWaits()
    {
        var webview = WebviewRebuildTests.TsCode("webview/main.ts");
        var hint = WebviewRebuildTests.Body(webview, "function renderPlaceholder(");
        // One card: the keys answer it (answerWaitingCard). Two: no key does, the turn's hint stays.
        Assert.Contains("approvalCards.size === 1 ? t('composerPlaceholderCard')", hint, StringComparison.Ordinal);
        Assert.Contains("!busy ? t('composerPlaceholder')", hint, StringComparison.Ordinal);

        // Shown, answered, retired: each change of the waiting cards redraws it, right where the set changes.
        Assert.Matches(new Regex(@"approvalCards\.set\(id, el\);\s*renderPlaceholder\(\);"), webview);
        Assert.Equal(2, Regex.Matches(webview, @"approvalCards\.delete\(id\);\s*renderPlaceholder\(\);").Count);
        Assert.Contains("renderPlaceholder()", WebviewRebuildTests.Body(webview, "function setBusy("), StringComparison.Ordinal);
        // That reader is the only writer.
        Assert.Single(Regex.Matches(webview, @"promptEl\.placeholder\s*="));
    }

    /// <summary>
    /// Opening Inferpal takes no key by default, in either editor: Alt+B opens the Build menu in English Visual Studio
    /// and Alt+O the Tools menu ("Outils") in French, and VS Code gives Ctrl+Alt+I to its own chat (Cmd+Alt+I to the
    /// developer tools on macOS). Tools → Inferpal → Inferpal Chat and the activity-bar icon open it — and no page that
    /// describes the product names one of those keys.
    /// </summary>
    [Fact]
    public void OpeningInferpal_TakesNoKeyTheEditorAlreadyGives()
    {
        var command = ConventionCoverageTests.CodeOnly(Path.Combine(Root, "Inferpal", "Commands", "OpenInferpalWindowCommand.cs"));
        Assert.Contains("CommandConfiguration", command, StringComparison.Ordinal);   // WITNESS
        Assert.DoesNotContain("Shortcuts", command, StringComparison.Ordinal);

        // The view answers to two commands: its own focus command and its activity-bar container's.
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "vscode", "package.json")));
        var bound = manifest.RootElement.GetProperty("contributes").GetProperty("keybindings").EnumerateArray()
            .Select(b => b.GetProperty("command").GetString()).ToList();
        Assert.Contains("inferpal.map", bound);   // WITNESS
        Assert.DoesNotContain("inferpal.chat.focus", bound);
        Assert.DoesNotContain("workbench.view.extension.inferpal", bound);

        var keys = new Regex(@"\balt\s*\+\s*[bo]\b|\b(ctrl|cmd)\s*\+\s*alt\s*\+\s*i\b", RegexOptions.IgnoreCase);
        Assert.Matches(keys, "press Alt + B");   // WITNESS
        Assert.Matches(keys, "cmd+alt+i");       // WITNESS
        var docs = DocCountersTests.LivingDocs().ToList();
        Assert.Contains(docs, d => d.EndsWith("getting-started.md", StringComparison.Ordinal));   // WITNESS
        Assert.Empty(docs.Where(d => keys.IsMatch(File.ReadAllText(d))).Select(Path.GetFileName));
    }
}
