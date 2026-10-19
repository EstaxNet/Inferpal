using System.IO;
using System.Text.RegularExpressions;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What is typed while a turn runs, in both editors: Enter is a note for the run, read before its next step; Ctrl+Enter a
/// message sent once the turn ends; the text is never dropped, and never stops the turn. A note is never a question —
/// every reader of the last question skips it —, survives a reload as a note, and comes back with its question when it is
/// asked again.
/// </summary>
/// <remarks>⚠ The view models read here have no test harness (Remote UI, the webview): these rules read the source.</remarks>
public class SteeringFrontEndTests
{
    private static string Cs(params string[] parts) =>
        ConventionCoverageTests.CodeOnly(Path.Combine([ConversationPersistenceSilenceTests.RepoRoot(), .. parts]));

    // ── Visual Studio ───────────────────────────────────────────────────────

    [Fact]
    public void VisualStudio_EnterDuringATurn_NeverStopsIt_ItSendsANote()
    {
        var send = WebviewRebuildTests.Body(Cs("Inferpal", "ToolWindow", "InferpalToolWindowData.ChatTurn.cs"),
                                            "private async Task SendAsync(object? _, CancellationToken ct)");
        var busy = WebviewRebuildTests.Body(send, "if (IsLoading)");

        Assert.Contains("Steer(text)", busy, StringComparison.Ordinal);   // witness: the branch is read
        Assert.DoesNotContain("_currentCts?.Cancel()", send, StringComparison.Ordinal);
    }

    [Fact]
    public void VisualStudio_ATextNeverApprovesAWaitingCard_AndRefusesItOnlyWhenTheRunReadsIt()
    {
        var busy = WebviewRebuildTests.Body(WebviewRebuildTests.Body(
            Cs("Inferpal", "ToolWindow", "InferpalToolWindowData.ChatTurn.cs"),
            "private async Task SendAsync(object? _, CancellationToken ct)"), "if (IsLoading)");

        // "Allow once" only with nothing typed.
        var once = WebviewRebuildTests.Body(busy, "if (_pendingApproval is { IsAnswered: false } card && text.Length == 0)");
        Assert.Contains("card.Answer(ApprovalDecision.Once);", once, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(busy, @"ApprovalDecision\.Once"));
        // Refused only after the text became a note the run will read — queued, it leaves the card waiting.
        var steered = busy.IndexOf("if (!Steer(text)) return;", StringComparison.Ordinal);
        // Withdrawn, not denied: a refusal for the call, never counted as the person's answer.
        var deny = busy.IndexOf("waiting.Answer(ApprovalDecision.Withdrawn);", StringComparison.Ordinal);
        Assert.True(steered > 0 && deny > steered, "a card is refused before the text is known to be read by the run");
    }

    [Fact]
    public void VisualStudio_ASlashCommandTypedDuringATurn_IsQueued_NeverANote()
    {
        var steer = WebviewRebuildTests.Body(Cs("Inferpal", "ToolWindow", "InferpalToolWindowData.Steering.cs"),
                                             "private bool Steer(string text)");
        Assert.Contains("if (_steering is { } steering && !text.StartsWith('/'))", steer, StringComparison.Ordinal);
    }

    [Fact]
    public void VisualStudio_TheQueueGivenBack_KeepsItsChips_AndItsEditorActions()
    {
        // Given back alone, the words of "Explain the code from Foo.cs" asked about code they no longer carried, as an
        // ordinary agent turn: chips go back with their message, an editor action stays in the queue.
        var giveBack = WebviewRebuildTests.Body(Cs("Inferpal", "ToolWindow", "InferpalToolWindowData.Steering.cs"),
                                                "private bool GiveQueueBack(");
        Assert.Contains("Attachments.Add(chip)", giveBack, StringComparison.Ordinal);
        Assert.Contains("q.Model is not null", giveBack, StringComparison.Ordinal);
        Assert.Contains("GiveQueueBack([.._queue])", WebviewRebuildTests.Body(
            Cs("Inferpal", "ToolWindow", "InferpalToolWindowData.Steering.cs"), "private void AfterTurn(bool normal)"), StringComparison.Ordinal);
    }

    [Fact]
    public void VisualStudio_AQueuedMessageRefusedAsItLeaves_IsGivenBack_NeverDropped()
    {
        var send = WebviewRebuildTests.Body(Cs("Inferpal", "ToolWindow", "InferpalToolWindowData.Steering.cs"),
                                            "private async Task SendQueuedAsync(QueuedMessage message)");
        var refused = WebviewRebuildTests.Body(send, "if (!message.Text.StartsWith('/') && !_isBackendReachable)");
        Assert.Contains("GiveQueueBack([message]);", refused, StringComparison.Ordinal);
    }

    [Fact]
    public void VisualStudio_ASendReleasesItsOwnClaimOnly()
    {
        // The turn a send ended starts the next queued message, which claims first: cleared by the first send's finally,
        // a second turn could start under it.
        var send = WebviewRebuildTests.Body(Cs("Inferpal", "ToolWindow", "InferpalToolWindowData.ChatTurn.cs"),
                                            "private async Task SendAsync(object? _, CancellationToken ct)");
        Assert.Contains("if (_sendClaim == claim) _sendStarting = false;", send, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"RunOnVMContextAsync\(\(\) => _sendStarting = false\)", send);
    }

    [Fact]
    public void VisualStudio_AFailedSlashCommand_GivesTheQueueBack()
    {
        // A turn's owner that throws marks the turn failed before releasing it: the queue then goes back to the input box.
        var owned = WebviewRebuildTests.Body(Cs("Inferpal", "ToolWindow", "InferpalToolWindowData.UiHelpers.cs"),
                                             "private async Task RunOwnedCommandAsync(");
        Assert.Contains("FailsTheTurn()", owned, StringComparison.Ordinal);
    }

    [Fact]
    public void VisualStudio_CtrlEnter_SendsAfterTheTurn()
    {
        var xaml = File.ReadAllText(Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(),
                                                 "Inferpal", "ToolWindow", "InferpalToolWindowContent.xaml"));
        Assert.Contains("<KeyBinding Key=\"Return\" Modifiers=\"Control\" Command=\"{Binding QueueCommand}\"/>", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void VisualStudio_AContextMenuActionDuringATurn_WaitsForIt()
    {
        var consume = WebviewRebuildTests.Body(Cs("Inferpal", "ToolWindow", "InferpalToolWindowData.PendingPrompt.cs"),
                                               "private void ConsumePendingPrompt()");
        Assert.Contains("Enqueue(new QueuedMessage(p, m, atts));", consume, StringComparison.Ordinal);
        Assert.DoesNotContain("_currentCts?.Cancel()", consume, StringComparison.Ordinal);
    }

    [Fact]
    public void VisualStudio_EveryTurnTakenReleasesTheQueue_WhereItIsReleased()
    {
        // A turn is taken by BeginOwnedTurn — a chat turn, a slash command, a code action — and released here only.
        var end = WebviewRebuildTests.Body(Cs("Inferpal", "ToolWindow", "InferpalToolWindowData.UiHelpers.cs"),
                                           "private void EndOwnedTurn(CancellationTokenSource? cts)");
        Assert.Contains("AfterTurn(normal: !stopped && !_turnFailed);", end, StringComparison.Ordinal);
    }

    [Fact]
    public void VisualStudio_EveryWayOutOfAChatTurn_SettlesItsNotes_BeforeReleasingIt()
    {
        var core = WebviewRebuildTests.Body(Cs("Inferpal", "ToolWindow", "InferpalToolWindowData.ChatTurn.cs"),
                                            "private async Task SendCoreAsync(");
        var ends = Regex.Matches(core, @"EndOwnedTurn\(localCts\);").Count;
        Assert.True(ends >= 3, "the turn's releases moved — the rule reads nothing");   // witness
        foreach (Match release in Regex.Matches(core, @"EndOwnedTurn\(localCts\);"))
        {
            var before = core[Math.Max(0, release.Index - 300)..release.Index];
            Assert.Contains("SettleNotes(steering,", before, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void VisualStudio_Regenerate_AsksTheQuestion_NeverANote_WithItsNotes()
    {
        var regenerate = WebviewRebuildTests.Body(Cs("Inferpal", "ToolWindow", "InferpalToolWindowData.ToolInvocation.cs"),
                                                  "private async Task RegenerateAsync()");
        Assert.Contains("Messages[i].Role == \"user\" && !Messages[i].IsNote", regenerate, StringComparison.Ordinal);
        Assert.Contains("notes: notes", regenerate, StringComparison.Ordinal);
    }

    // ── VS Code ─────────────────────────────────────────────────────────────

    [Fact]
    public void VsCode_DuringATurn_TheComposerNeverDropsTheText()
    {
        var send = WebviewRebuildTests.Body(WebviewRebuildTests.TsCode("webview/main.ts"), "function send(later = false): void");

        Assert.DoesNotContain("|| busy)", send, StringComparison.Ordinal);
        Assert.Contains("post(later ? { type: 'queue', text } : { type: 'steer', text });", send, StringComparison.Ordinal);
        Assert.Contains("send(e.ctrlKey || e.metaKey);", WebviewRebuildTests.TsCode("webview/main.ts"), StringComparison.Ordinal);
        // The webview never answers a card for a note: the extension does, once the host took the note.
        Assert.DoesNotContain("answerWaitingCard(", send, StringComparison.Ordinal);
    }

    [Fact]
    public void VsCode_ANote_RefusesAWaitingCard_OnlyOnceTheRunTookIt_AndASlashCommandIsQueued()
    {
        var steer = WebviewRebuildTests.Body(WebviewRebuildTests.TsCode("chatViewProvider.ts"), "private async steer(text: string)");

        var slash = steer.IndexOf("if (prompt.startsWith('/')) {", StringComparison.Ordinal);
        var asked = steer.IndexOf("host.chatSteer(id, prompt)", StringComparison.Ordinal);
        Assert.True(slash > 0 && slash < asked, "a slash command typed during a turn is sent to the run as a note");
        var accepted = WebviewRebuildTests.Body(steer, "if (accepted)");
        Assert.Contains("this.dismissApproval(", accepted, StringComparison.Ordinal);
        // Withdrawn (4), not denied (0): never counted as the person's answer (RpcApprovalService reads 4 as Withdrawn).
        Assert.Contains("this.dismissApproval([...this.pendingApprovals.keys()][0], 4);", accepted, StringComparison.Ordinal);
        Assert.Contains("4 => ApprovalDecision.Withdrawn", File.ReadAllText(Path.Combine(
            ConventionCoverageTests.RepoRoot(), "Inferpal.Host", "RpcApprovalService.cs")), StringComparison.Ordinal);
        Assert.Contains("chatResumeStep()", accepted, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(steer, @"dismissApproval\("));
    }

    [Fact]
    public void VsCode_TheNextQueuedMessage_LeavesAfterTheSendThatEnded_NeverInsideIt()
    {
        var after = WebviewRebuildTests.Body(WebviewRebuildTests.TsCode("chatViewProvider.ts"), "private afterTurn(): void");
        Assert.Contains("setTimeout(() => {", after, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"^\s*void this\.send\(next\.text, next\.typed\);\s*\n\s*return;", after);
    }

    [Fact]
    public void VsCode_TheExport_CountsANote_InItsTurn()
    {
        var provider = WebviewRebuildTests.TsCode("chatViewProvider.ts");
        Assert.Contains("notice: item.notice === true || item.note !== undefined,", provider, StringComparison.Ordinal);
    }

    [Fact]
    public void VsCode_ANote_IsSavedAsANote_AndReadBackAsOne()
    {
        var sessions = WebviewRebuildTests.TsCode("chatSessions.ts");

        Assert.Contains($"export const NOTE_MARKER = '{SessionManager.NoteMarker}';", sessions, StringComparison.Ordinal);
        Assert.Contains("item.note === 'read'", WebviewRebuildTests.Body(sessions, "export function toSavedMessages("), StringComparison.Ordinal);
        Assert.Contains("m.toolName === NOTE_MARKER", WebviewRebuildTests.Body(sessions, "export function toTranscript("), StringComparison.Ordinal);
    }

    [Fact]
    public void VsCode_TheLastQuestion_IsNeverANote()
    {
        var provider = WebviewRebuildTests.TsCode("chatViewProvider.ts");

        Assert.DoesNotMatch(@"reverse\(\)\.find\(\(m\) => m\.role === 'user'\)", provider);
        Assert.Contains("return m.role === 'user' && !m.note", WebviewRebuildTests.Body(provider, "private static isModelQuestion("),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void VsCode_EveryTurnEnd_SettlesItsNotes_AndSaysHowItEnded()
    {
        // The next queued message goes after a turn that ended normally only: each place a turn ends says how.
        var provider = WebviewRebuildTests.TsCode("chatViewProvider.ts");
        var posts = Regex.Matches(provider, @"type: 'turnEnded'").Count;
        Assert.True(posts >= 4, "the turn ends moved — the rule reads nothing");   // witness
        Assert.Equal(posts, Regex.Matches(provider, @"this\.turnEnded\('|this\.turnEnded\(result\.|this\.turnEnded\(error").Count);
        Assert.Equal(posts, Regex.Matches(provider, @"this\.settleNotes\(").Count);
    }
}
