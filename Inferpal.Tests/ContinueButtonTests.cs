using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Inferpal.Host;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// "Ask it to continue" is a gesture: the end notice of a turn stopped at the iteration limit, or whose answer stopped at
/// the length limit, offers Continue — never after a loop or a repetition — and Continue is the question it would type,
/// sent by the ordinary funnel of each editor.
/// </summary>
public sealed class ContinueButtonTests
{
    [Theory]
    [InlineData(true,  false, false, false, true)]    // the iteration limit
    [InlineData(false, false, true,  false, true)]    // the length limit
    [InlineData(true,  false, true,  false, true)]    // both
    [InlineData(false, false, false, false, false)]   // reference arm: a task carried to its end
    [InlineData(false, true,  false, false, false)]   // a loop: continuing would do the same again
    [InlineData(false, true,  true,  false, false)]   // a loop whose last answer was cut too: the loop's verdict stands
    [InlineData(false, false, true,  true,  false)]   // stopped repeating itself: same
    [InlineData(true,  false, true,  true,  false)]   // a synthesis at the limit that repeated itself
    public void TheNoticeOffersContinue_OnlyWhereContinuingCanHelp(
        bool limit, bool loop, bool cut, bool repeating, bool offered) =>
        Assert.Equal(offered, ChatTurnPolicy.OffersContinue(limit, loop, cut, repeating));

    [Fact]
    public void TheHost_SaysWhetherToOfferContinue_WhereverItSaysHowTheTurnEnded()
    {
        var host = File.ReadAllText(Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal.Host", "HostServer.cs"));
        var ended = Regex.Matches(host, @"Ended: Ended\(").Count;
        Assert.True(ended >= 3, $"only {ended} turn end(s) read in HostServer.cs: the scan is blind.");
        Assert.Equal(ended, Regex.Matches(host, @"OffersContinue: ChatTurnPolicy\.OffersContinue\(").Count);
    }

    [Fact]
    public void VisualStudio_OffersContinueOnTheEndNotice_AndSendsItByTheOrdinaryFunnel()
    {
        var turn = File.ReadAllText(Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.ChatTurn.cs"));
        Assert.Equal(2, Regex.Matches(turn, @"agentOffersContinue = ChatTurnPolicy\.OffersContinue\(").Count);   // both loops
        Assert.Contains("if (agentOffersContinue && !agentFailed) MarkContinuable(endNotice);", turn, StringComparison.Ordinal);

        var invocation = File.ReadAllText(Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.ToolInvocation.cs"));
        Assert.Matches(new Regex(@"await SendCoreAsync\(Strings\.ContinueQuestion, oneTimeModel, attachments: \[\],"), invocation);
        Assert.Contains("oneTimeModel = _lastSent?.Model;", invocation, StringComparison.Ordinal);   // a code action continues as one
        Assert.Contains("clearPrompt: false, typed: false);", invocation, StringComparison.Ordinal);   // not typed: no prompt history
        Assert.Contains("if (_sendStarting || IsLoading || _currentCts is not null || _lastContinuableMsg is null) return;", invocation,
                        StringComparison.Ordinal);

        // Another turn takes it off: the turn's funnel forgets it.
        var helpers = File.ReadAllText(Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.UiHelpers.cs"));
        var begin = helpers[helpers.IndexOf("private CancellationTokenSource BeginOwnedTurn", StringComparison.Ordinal)..];
        Assert.Contains("ForgetContinuable();", begin[..begin.IndexOf("return cts;", StringComparison.Ordinal)], StringComparison.Ordinal);

        var xaml = File.ReadAllText(Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowContent.xaml"));
        Assert.Contains("Command=\"{Binding ContinueCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding IsContinuable, Converter={StaticResource b2v}}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void VsCode_OffersContinueOnTheEndNotice_TakesItOffAtTheNextTurn_AndSendsTheQuestion()
    {
        var root = ConventionCoverageTests.RepoRoot();
        var webview = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(root, "vscode", "src", "webview", "main.ts")));
        Assert.Contains("continues: msg.offersContinue === true,", webview, StringComparison.Ordinal);
        var setBusy = webview[webview.IndexOf("function setBusy(value: boolean): void {", StringComparison.Ordinal)..];
        Assert.Contains("forgetContinue();", setBusy[..setBusy.IndexOf('}')], StringComparison.Ordinal);

        var provider = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(root, "vscode", "src", "chatViewProvider.ts")));
        Assert.Matches(new Regex(@"case 'continue':\s*await this\.continueTurn\(\);"), provider);
        // Replayed (no prompt history, no slash reading), a code action as one, none of the composer's chips.
        Assert.Contains("await this.send(prompt, false, { prompt, codeAction: this.lastSent?.codeAction === true, query: prompt });",
                        provider, StringComparison.Ordinal);
        Assert.Contains("this.continuing ? [] : this.pendingAttachments", provider, StringComparison.Ordinal);
        // A refused send keeps the button: only the turn's start takes it off. Drawn from the item, a redraw keeps it.
        Assert.Contains("go.addEventListener('click', () => post({ type: 'continue' }));", webview, StringComparison.Ordinal);
        Assert.Contains("if (item.continues && !busy) {", webview, StringComparison.Ordinal);
        Assert.Contains("continues: result.offersContinue === true,", provider, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"this\.busy = true;\s*this\.stopPressed = false;\s*for \(const item of this\.transcript\) \{\s*delete item\.continues;"), provider);
        // Never saved: the transcript-to-session mapping lists what it keeps.
        var sessions = File.ReadAllText(Path.Combine(root, "vscode", "src", "chatSessions.ts"));
        Assert.DoesNotContain("continues", sessions, StringComparison.Ordinal);
        Assert.Contains("offersContinue: !result.error && result.offersContinue === true,", provider, StringComparison.Ordinal);
    }
}

public partial class HostServerTests
{
    [Fact]
    public async Task ChatSend_AnAnswerCutAtTheLengthLimit_OffersContinue_AndAFinishedOneDoesNot()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        h.Fake.ChatResult = new ChatTurnResult("Here is the fix:\n```csharp\npublic int Add(int a,", null, 0, 0, CutAtLimit: true);
        var cut = await Send(h, "fix it");
        Assert.True(cut.OffersContinue);

        h.Fake.ChatResult = new ChatTurnResult("Here is the fix.", null, 0, 0);
        Assert.False((await Send(h, "and the other one")).OffersContinue);   // reference arm

        h.Fake.ChatResult = new ChatTurnResult("again again again", null, 0, 0, CutAtLimit: true, StoppedRepeating: true);
        Assert.False((await Send(h, "once more")).OffersContinue);           // continuing would repeat
    }

    [Fact]
    public async Task ChatSend_WithToolsOff_ACutAnswerOffersContinue_AsWithTools()
    {
        // The plain chat path (no tool offered) has its own end: the same decision, read there too.
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        h.Server.CurrentSession!.ToolsEnabled = false;
        h.Fake.ChatResult = new ChatTurnResult("Here is the start of it,", null, 0, 0, CutAtLimit: true);
        Assert.True((await Send(h, "write the whole guide")).OffersContinue);

        h.Fake.ChatResult = new ChatTurnResult("Here it is, whole.", null, 0, 0);
        Assert.False((await Send(h, "and the other")).OffersContinue);   // reference arm
    }

    [Fact]
    public async Task ChatSend_ARunAtItsIterationLimit_OffersContinue_AndARunStoppedOnALoopDoesNot()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        h.Server.CurrentSession!.ToolsEnabled = true;

        h.Fake.AgentRunResult = new AgentResult(Strings.MsgIterationLimit(5), [new ToolExecution("read_file", "{}", "x")], [],
                                                ReachedIterationLimit: true);
        Assert.True((await Send(h, "fix every warning")).OffersContinue);

        h.Fake.AgentRunResult = new AgentResult("stuck", [new ToolExecution("read_file", "{}", "x")], [], WasLoopDetected: true);
        Assert.False((await Send(h, "and now")).OffersContinue);
    }

    /// <summary>
    /// ⚠ Continue is a question on the conversation as it stands: the request is the one before plus the answer and the
    /// question, its system message the same to the byte — a server that keeps its prompt cache reads only what is new.
    /// </summary>
    [Fact]
    public async Task Continue_SendsTheConversationAsItStands_ItsStartUnchanged()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var requests = new List<List<ChatMessageDto>>();
        h.Fake.OnChatRequest = (_, messages, _, _) =>
        {
            requests.Add([.. messages.Select(m => new ChatMessageDto(m.Role, m.Content))]);
            return Task.FromResult(new ChatTurnResult(requests.Count == 1 ? "Here is the start of it," : "and the end.", null, 0, 0,
                                                      CutAtLimit: requests.Count == 1));
        };

        Assert.True((await Send(h, "write the whole guide")).OffersContinue);
        await Send(h, Strings.ContinueQuestion);

        Assert.Equal(2, requests.Count);
        var (first, second) = (requests[0], requests[1]);
        Assert.Equal("system", second[0].Role);
        Assert.Equal(first[0].Content, second[0].Content);   // the system message, to the byte
        for (var i = 0; i < first.Count; i++)
            Assert.Equal((first[i].Role, first[i].Content), (second[i].Role, second[i].Content));
        Assert.Equal(("assistant", "user"), (second[first.Count].Role, second[^1].Role));
        Assert.EndsWith(Strings.ContinueQuestion, second[^1].Content, StringComparison.Ordinal);
    }

    private static Task<ChatSendResult> Send(Harness h, string prompt) =>
        h.Client.InvokeWithParameterObjectAsync<ChatSendResult>("chat/send", new { prompt, agentMode = false })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
}
