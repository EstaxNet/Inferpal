using System.IO;
using System.Text.Json;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The key of inline completion in both editors: VS Code's provider tells a key from a pause, asks the host for the
/// key's sentence, and stops asking after a pause on demand; the key is the same in Visual Studio and VS Code.
/// </summary>
/// <remarks>VS Code's provider ignored <c>triggerKind</c>: there was no key, and no setting could keep a pause from
/// asking. The host and Visual Studio halves are tested where they run (<c>HostServerTests.FimAsk_*</c>,
/// <see cref="InProcConfigKeyTests"/>, <see cref="CompletionAskSignalTests"/>).</remarks>
public sealed class CompletionOnDemandFrontEndTests
{
    private static string Root => ConversationPersistenceSilenceTests.RepoRoot();
    private static string Provider => Path.Combine(Root, "vscode", "src", "inlineCompletions.ts");

    [Fact]
    public void TheVsCodeProvider_TellsAKeyFromAPause_BeforeItWaits()
    {
        var body = ConversationPersistenceSilenceTests.TsMethodBody(Provider, "async provideInlineCompletionItems(");

        var key   = body.IndexOf("context.triggerKind === vscode.InlineCompletionTriggerKind.Invoke", StringComparison.Ordinal);
        var asks  = body.IndexOf("return this.ask(", StringComparison.Ordinal);
        var pause = body.IndexOf("await delay(", StringComparison.Ordinal);
        Assert.True(pause > 0, "the pause is gone from the provider: check what replaced it.");   // witness
        Assert.InRange(key, 0, pause);
        Assert.InRange(asks, key, pause);
    }

    [Fact]
    public void OnDemand_APauseAsksForNothing_InVsCode()
    {
        var body = ConversationPersistenceSilenceTests.TsMethodBody(Provider, "async provideInlineCompletionItems(");

        var onDemand = body.IndexOf("settings.onDemand", StringComparison.Ordinal);
        Assert.InRange(onDemand, 0, body.IndexOf("await delay(", StringComparison.Ordinal));
    }

    [Fact]
    public void TheKey_GoesThroughTheHost_AndSaysWhatItBrings()
    {
        var ask = ConversationPersistenceSilenceTests.TsMethodBody(Provider, "private async ask(");

        Assert.Contains("host.fimAsk(", ask, StringComparison.Ordinal);
        Assert.Contains("say(result.notice)", ask, StringComparison.Ordinal);
        Assert.Contains("say(hostErrorText(err))", ask, StringComparison.Ordinal);
        Assert.DoesNotContain("await delay(", ask, StringComparison.Ordinal);   // the person is waiting
    }

    /// <summary>Visual Studio's half runs in devenv, where no test drives an editor: what it decides is held at the
    /// source — a pause asks only when not on demand, the key takes a suggestion shown before anything else, asks off
    /// the UI thread, past an open list, and says what it brings back.</summary>
    [Fact]
    public void VisualStudiosGhostText_FollowsTheTrigger_AndTheKey()
    {
        var controller = ConventionCoverageTests.CodeOnly(Path.Combine(Root, "Inferpal.InProc", "GhostText", "GhostTextController.cs"));
        var watcher    = ConventionCoverageTests.CodeOnly(Path.Combine(Root, "Inferpal.InProc", "GhostText", "CompletionAskWatcher.cs"));

        Assert.Matches(@"void OnTextChanged\([\s\S]*?config\.AsksOnPause[\s\S]*?new Timer\(", controller);
        Assert.Matches(@"internal void Ask\(CompletionAsk ask\)[\s\S]*?PendingCompletion[\s\S]*?AcceptCompletion[\s\S]*?ask\.Refusal"
                       + @"[\s\S]*?Task\.Run\(\(\) => TriggerAsync\(ask\)\)", controller);
        Assert.Contains("if (!asked && !GhostTextTrigger.Asks(", controller, StringComparison.Ordinal);
        Assert.Contains("answer.Failed ? asked.Failed : asked.Nothing", controller, StringComparison.Ordinal);
        Assert.Contains("await SayAsync(ask.Refusal ?? ask.NoEditor)", watcher, StringComparison.Ordinal);
    }

    [Fact]
    public void TheKey_IsAltQuote_InBothEditors_AskingOrTakingTheSuggestionShown()
    {
        using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "vscode", "package.json")));
        var bindings = package.RootElement.GetProperty("contributes").GetProperty("keybindings").EnumerateArray()
            .Where(b => b.GetProperty("key").GetString() == "alt+'")
            .ToDictionary(b => b.GetProperty("command").GetString()!, b => b.GetProperty("when").GetString()!);

        Assert.Equal(2, bindings.Count);
        Assert.Contains("!inlineSuggestionVisible", bindings["editor.action.inlineSuggest.trigger"], StringComparison.Ordinal);
        Assert.Contains("inlineSuggestionVisible", bindings["editor.action.inlineSuggest.commit"], StringComparison.Ordinal);
        Assert.DoesNotContain("!inlineSuggestionVisible", bindings["editor.action.inlineSuggest.commit"], StringComparison.Ordinal);

        // VK_OEM_7 is the key VS Code calls "'" on a US keyboard.
        var command = ConventionCoverageTests.CodeOnly(Path.Combine(Root, "Inferpal", "Commands", "AskCompletionCommand.cs"));
        Assert.Contains("new CommandShortcutConfiguration(ModifierKey.LeftAlt, Key.VK_OEM_7)", command, StringComparison.Ordinal);
    }
}
