using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A rebuilt VS Code webview does not lose what it was showing: an approval card lives exactly as
/// long as the question it asks, the pause banner as long as the pause, and a hidden form keeps what
/// is not saved yet.
/// </summary>
/// <remarks>
/// <para>
/// The webview does not keep cards in the transcript: <c>renderTranscript</c> empties the message
/// list and rebuilds it from the bubbles. Any rehydration while an approval waits (saving the
/// settings, a backend connection edge, a webview reload) therefore wiped the card while the host
/// still waits for its answer — with no timeout: the agent stays blocked on a question nobody can
/// see any more. Saving the settings is the most likely gesture at that moment (adding the
/// <c>allow</c> rule you are being asked about).
/// </para>
/// <para>
/// Reverse half: a turn whose request fails (host crashed, host restarted) leaves a card that
/// answers into the void — the §27.5 ghost card, through the crash path instead of cancellation.
/// </para>
/// <para>
/// ⚠ These rules read the source: the extension has no TypeScript test runner.
/// </para>
/// </remarks>
public class WebviewRebuildTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string TsCode(string relative)
    {
        var path = Path.Combine(RepoRoot(), "vscode", "src",
                                relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"vscode/src/{relative} is gone.");
        return SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(path));
    }

    // The settings panel sends back the WHOLE object it opened: without that starting JSON the host
    // copies everything, and a change made elsewhere since then is reverted.
    [Fact]
    public void TheSettingsPanel_SavesAgainstTheJsonItOpenedWith()
    {
        var panel = TsCode("settingsPanel.ts");
        Assert.Contains("host.configUpdate(", panel, StringComparison.Ordinal);
        Assert.Matches(@"host\.configUpdate\(\s*msg\.json\s*,\s*this\.lastConfigJson\s*\)", panel);

        Assert.Matches(@"'config/update',\s*\{\s*json,\s*base\s*\}", TsCode("hostClient.ts"));
    }

    /// <summary>Body of a TypeScript function, by brace matching from its signature.</summary>
    private static string Body(string source, string signature)
    {
        var at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"\"{signature}\" not found — the rule no longer measures anything.");

        var open  = source.IndexOf('{', at + signature.Length);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[(open + 1)..i];
        }
        Assert.Fail($"body of \"{signature}\" never closes.");
        return string.Empty;
    }

    [Fact]
    public void AHydratedView_ShowsTheApprovalCardsStillWaiting()
    {
        var provider = TsCode("chatViewProvider.ts");
        var webview  = TsCode("webview/main.ts");

        // Witness: rehydration does wipe everything the transcript does not carry.
        Assert.Contains("messagesEl.textContent = ''", Body(webview, "function renderTranscript("),
                        StringComparison.Ordinal);

        var hydrate = Body(provider, "private hydrate(): void");
        Assert.Contains("type: 'hydrate'", hydrate, StringComparison.Ordinal);

        Assert.True(Regex.IsMatch(hydrate, @"this\.pendingApprovals[\s\S]{0,200}?type: 'approval'"),
            "hydrate() does not re-post the cards still waiting: a rehydration during an approval "
            + "wipes the card and leaves the agent waiting for an answer nobody can give.");
    }

    /// <summary>
    /// Same class, another element the transcript does not carry: the step-by-step banner, the only
    /// place that holds the Resume button. Wiped during a pause, it leaves only cancellation.
    /// </summary>
    [Fact]
    public void AHydratedView_ShowsTheStepPauseStillInForce()
    {
        var provider = TsCode("chatViewProvider.ts");

        // Witness: the pause is relayed to the webview, and the Resume button lives in its banner.
        Assert.Contains("type: 'stepPaused'", provider, StringComparison.Ordinal);
        Assert.Contains("type: 'resumeStep'", TsCode("webview/main.ts"), StringComparison.Ordinal);

        Assert.True(Regex.IsMatch(Body(provider, "private hydrate(): void"),
                                  @"this\.stepPaused[\s\S]{0,120}?type: 'stepPaused'"),
            "hydrate() does not re-post the pause banner: a rehydration during a step-by-step pause "
            + "removes the only Resume button.");

        // And the banner does not outlive its turn (cancelled while paused, host crashed): the
        // webview removes it on `stepResumed` only, not on `turnEnded`.
        Assert.True(Regex.IsMatch(Body(provider, "private async chatTurn("),
                                  @"finally[\s\S]*?this\.stepPaused[\s\S]{0,160}?type: 'stepResumed'"),
            "The end of the turn does not retire the pause banner: a turn cancelled during the pause "
            + "leaves a Resume button that resumes nothing.");
    }

    /// <summary>
    /// A /branch that fails says why. For <c>/branch &lt;n&gt;</c> the host sends no bubble of its own,
    /// only the <c>branchRequest</c> effect: a fork refused (turn not found, a turn already running) or
    /// failing pushed no note, and the turn closed empty — nothing happened, the cause only in the log.
    /// </summary>
    [Fact]
    public void AFailedBranch_SaysWhy_InsteadOfAnEmptyBubble()
    {
        var provider = TsCode("chatViewProvider.ts");
        var branch   = Body(provider, "private async branchAtTurn(");

        // Witness: the fork still goes through session/branch.
        Assert.Contains("host.sessionBranch(", branch, StringComparison.Ordinal);

        Assert.DoesNotContain("return null", branch, StringComparison.Ordinal);
        Assert.Contains("hostUnavailableMessage()", branch, StringComparison.Ordinal);
        Assert.Contains("ChatViewProvider.errorText(err)", branch, StringComparison.Ordinal);
        Assert.Contains("vscode.l10n.t('No turn {0} in this conversation", branch, StringComparison.Ordinal);

        var effect = Regex.Match(provider, @"case 'branchRequest':[\s\S]*?break;");
        Assert.True(effect.Success, "the branchRequest effect is gone — the rule measures nothing.");
        Assert.Contains("notes.push(", effect.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("if (note)", effect.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// A /branch &lt;name&gt; that fails does not claim the switch. The host has already answered "Switched
    /// to branch X" when the adapter loads the session: a load that returned nothing or threw (a reply still
    /// running holds the host's turn slot) was only logged, and the bubble announced a switch that had not
    /// happened.
    /// </summary>
    [Fact]
    public void AFailedBranchSwitch_DropsTheHostsClaim_AndSaysWhy()
    {
        var provider = TsCode("chatViewProvider.ts");
        var sw       = Body(provider, "private async switchToSession(");

        // Witness: the switch still goes through session/load.
        Assert.Contains("host.sessionLoad(", sw, StringComparison.Ordinal);

        Assert.Contains("hostUnavailableMessage()", sw, StringComparison.Ordinal);
        Assert.Contains("ChatViewProvider.errorText(err)", sw, StringComparison.Ordinal);
        Assert.Contains("vscode.l10n.t('Branch {0} could not be loaded", sw, StringComparison.Ordinal);

        var effect = Regex.Match(provider, @"case 'loadSession':[\s\S]*?break;");
        Assert.True(effect.Success, "the loadSession effect is gone — the rule measures nothing.");
        Assert.Contains("notes.push(", effect.Value, StringComparison.Ordinal);
        Assert.Contains("dropHostMarkdown = true", effect.Value, StringComparison.Ordinal);

        // The bubble is built without the host's markdown once an effect failed.
        Assert.Contains("outcome.dropHostMarkdown ? '' : slash.markdown", provider, StringComparison.Ordinal);
    }

    /// <summary>
    /// Loading a session from the palette that fails says so. A session deleted between the pick and the
    /// load came back null and the command did nothing at all; a reply still running (the host's turn slot)
    /// surfaced as a raw command error.
    /// </summary>
    [Fact]
    public void AFailedSessionLoad_FromThePalette_SaysWhy()
    {
        var load = Body(TsCode("chatViewProvider.ts"), "async loadSessionCommand(");

        // Witness: the command still goes through session/load.
        Assert.Contains("host.sessionLoad(", load, StringComparison.Ordinal);

        Assert.Contains("vscode.l10n.t('Session {0} could not be loaded", load, StringComparison.Ordinal);
        Assert.Contains("ChatViewProvider.errorText(err)", load, StringComparison.Ordinal);
    }

    /// <summary>
    /// Deleting a session from the palette cannot be undone: the Visual Studio window confirms before
    /// the same File.Delete, VS Code deleted as soon as an item was picked. And the boolean session/delete
    /// returns was ignored — success and an already-gone session both answered with silence.
    /// </summary>
    [Fact]
    public void ASessionDelete_FromThePalette_ConfirmsThenSaysWhatHappened()
    {
        var delete = Body(TsCode("chatViewProvider.ts"), "async deleteSessionCommand(");

        // Witness: the command still goes through session/delete.
        var call = delete.IndexOf("host.sessionDelete(", StringComparison.Ordinal);
        Assert.True(call >= 0, "deleteSessionCommand no longer calls host.sessionDelete");

        // A modal confirmation BEFORE the call.
        var confirm = delete.IndexOf("vscode.l10n.t('Delete session {0}? This cannot be undone.'", StringComparison.Ordinal);
        Assert.True(confirm >= 0 && confirm < call, "the deletion is not confirmed before session/delete");
        Assert.Contains("modal: true", delete, StringComparison.Ordinal);

        // Both outcomes are stated, and a failure names its cause.
        Assert.Contains("vscode.l10n.t('Session deleted: {0}'", delete, StringComparison.Ordinal);
        Assert.Contains("vscode.l10n.t('Session {0} was not found", delete, StringComparison.Ordinal);
        Assert.Contains("ChatViewProvider.errorText(err)", delete, StringComparison.Ordinal);
    }

    /// <summary>
    /// The session list describes each entry by its message count: a branch's row went through l10n, a
    /// root session's wrote "msg" hard-coded — "12 件" and "12 msg" one under the other.
    /// </summary>
    [Fact]
    public void ASessionPicker_DescribesEveryRow_InTheSameLanguage()
    {
        var picker = Body(TsCode("chatSessions.ts"), "export async function pickSession(");

        // Witness: a branch's description goes through l10n.
        Assert.Contains("vscode.l10n.t('{0} msg · from {1} @ turn {2}'", picker, StringComparison.Ordinal);

        Assert.Contains("vscode.l10n.t('{0} msg', s.messageCount)", picker, StringComparison.Ordinal);
        Assert.DoesNotContain("} msg`", picker, StringComparison.Ordinal);
    }

    /// <summary>
    /// String() of an Error returns its toString() — "Error: &lt;message&gt;". Posted to the settings
    /// panel's status or slipped into a translated message, it glued an English word in front of an
    /// already translated refusal (saving during a reply). Log lines keep their String(err).
    /// </summary>
    [Fact]
    public void AnErrorShownToTheUser_CarriesItsMessage_NotItsToString()
    {
        var logged    = 0;
        var offenders = new List<string>();
        foreach (var file in new[] { "settingsPanel.ts", "chatViewProvider.ts", "extension.ts" })
        {
            var code = TsCode(file);
            // Witness: log lines keep String(err) — the scan does read these files.
            logged += System.Text.RegularExpressions.Regex.Matches(code, @"log\(`[^`]*\$\{String\(err\)\}").Count;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         code, @"message:\s*String\(err\)|show\w+Message\([^;]*String\(err\)"))
                offenders.Add($"{file}: {m.Value}");
        }

        Assert.True(logged >= 5, $"only {logged} log line(s) with String(err) found: the scan reads nothing");
        Assert.Empty(offenders);
    }

    /// <summary>
    /// Same class, in the settings panel: it opens as an editor tab, and VS Code destroys a hidden
    /// tab's webview unless asked to keep it. On return the form reloads from the config and unsaved
    /// edits vanish without a word — clicking Save afterwards writes the old values.
    /// </summary>
    [Fact]
    public void TheSettingsForm_KeepsUnsavedEditsWhileItsTabIsHidden()
    {
        var panel = TsCode("settingsPanel.ts");
        var form  = TsCode("webview/settings.ts");

        // Witness: the form writes only on an explicit Save, so it does hold unsaved state — and it
        // does not keep that state in the webview state itself.
        Assert.Contains("type: 'save'", form, StringComparison.Ordinal);
        Assert.DoesNotContain("setState(", form, StringComparison.Ordinal);

        var created = Regex.Match(panel, @"createWebviewPanel\(([\s\S]*?)\);");
        Assert.True(created.Success, "createWebviewPanel not found — the rule no longer measures anything.");

        Assert.True(Regex.IsMatch(created.Groups[1].Value, @"retainContextWhenHidden:\s*true"),
            "The settings panel does not keep its hidden webview: switching tabs erases unsaved edits.");
    }

    [Fact]
    public void AFailedTurn_RetiresItsApprovalCards()
    {
        var chatTurn = Body(TsCode("chatViewProvider.ts"), "private async chatTurn(");

        // Witness: the turn still has its failure path.
        var catchAt = chatTurn.IndexOf("catch (err)", StringComparison.Ordinal);
        Assert.True(catchAt >= 0, "chatTurn has no catch any more — the rule no longer measures anything.");

        Assert.True(chatTurn[catchAt..].Contains("this.dismissAllPending()", StringComparison.Ordinal),
            "A turn whose request failed (host crashed or restarted) leaves its approval cards "
            + "clickable, while nobody waits for their answer any more.");
    }

    /// <summary>
    /// The archive of the conversation being left does not become the file of the next one: the host
    /// binds every named save to the current conversation, and /branch rewrites that file.
    /// </summary>
    [Fact]
    public void TheArchiveOfALeftConversation_IsSavedAsAnArchive()
    {
        var provider = TsCode("chatViewProvider.ts");

        var saveCommand = Body(provider, "async saveSessionCommand(");
        Assert.Contains("host.sessionSave(safeName, this.snapshot())", saveCommand, StringComparison.Ordinal);

        var archive = Body(provider, "private archiveConversation(");
        Assert.Contains("host.sessionSave(fileName, messages, true)", archive, StringComparison.Ordinal);
        Assert.Matches(@"'session/save',\s*\{\s*name,\s*messages,\s*archive\s*\}", TsCode("hostClient.ts"));
    }

    /// <summary>
    /// Typing /clear (or applying a /template) emptied the transcript without archiving it: the
    /// new-conversation button archives first, like the Visual Studio window, and the conversation
    /// vanished from the saved sessions.
    /// </summary>
    [Fact]
    public void ClearingTheChatWithACommand_ArchivesTheConversationFirst()
    {
        var provider = TsCode("chatViewProvider.ts");

        var reset        = Body(provider, "async resetConversation(");
        var resetArchive = reset.IndexOf("this.archiveConversation()", StringComparison.Ordinal);
        Assert.True(resetArchive >= 0 && resetArchive < reset.IndexOf("this.transcript.length = 0", StringComparison.Ordinal),
            "resetConversation no longer archives before clearing: the rule has no reference");

        var effect = Regex.Match(provider, @"case 'clearTranscript':([\s\S]*?)break;");
        Assert.True(effect.Success, "the clearTranscript effect is gone: the rule measures nothing");
        var archive = effect.Groups[1].Value.IndexOf("this.archiveConversation()", StringComparison.Ordinal);
        var clear   = effect.Groups[1].Value.IndexOf("this.transcript.length = 0", StringComparison.Ordinal);
        Assert.True(archive >= 0 && archive < clear,
            "/clear and /template empty the transcript without archiving it, unlike the reset button");
    }

    /// <summary>
    /// Saving a session under a name already taken replaced the other conversation without a word:
    /// deleting one asks for confirmation first — and it is the same loss.
    /// </summary>
    [Fact]
    public void SavingUnderANameAlreadyTaken_AsksBeforeReplacing()
    {
        var provider = TsCode("chatViewProvider.ts");

        Assert.Contains("vscode.l10n.t('Delete session {0}? This cannot be undone.'",
            Body(provider, "async deleteSessionCommand("), StringComparison.Ordinal);

        var save    = Body(provider, "async saveSessionCommand(");
        var list    = save.IndexOf("host.sessionList()", StringComparison.Ordinal);
        var confirm = save.IndexOf("vscode.l10n.t('A session named {0} already exists. Replace it?'", StringComparison.Ordinal);
        var write   = save.IndexOf("host.sessionSave(", StringComparison.Ordinal);
        Assert.True(list >= 0 && confirm > list && write > confirm,
            "saving under an existing name replaces that session without asking");
        Assert.Contains("modal: true", save, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bounded transcript says how much it dropped, and nothing more. Past the cap every append
    /// spliced the previous marker out with the oldest entries, so the count restarted at 1; and the
    /// marker promised the full conversation was in the saved session, which saves this same trimmed
    /// transcript.
    /// </summary>
    [Fact]
    public void TheTranscriptCap_CountsEverythingItDropped_AndPromisesNoFullCopy()
    {
        var provider = TsCode("chatViewProvider.ts");
        var trim     = Body(provider, "private trimTranscript(): void");

        // Witness: the saved session really is the live (trimmed) transcript.
        Assert.Contains("toSavedMessages(this.transcript)", Body(provider, "private snapshot("),
                        StringComparison.Ordinal);

        Assert.Matches(@"this\.droppedEntries\s*\+=", trim);
        Assert.Contains("this.transcript[0] =", trim, StringComparison.Ordinal);
        Assert.DoesNotContain("the full conversation is in the saved session", provider, StringComparison.Ordinal);

        // A new conversation starts a new count.
        var resets = Regex.Matches(provider, @"this\.transcript\.length = 0;");
        Assert.True(resets.Count >= 3, "transcript reset sites not found: the rule measures nothing");
        foreach (Match reset in resets)
            Assert.True(Regex.IsMatch(provider[reset.Index..Math.Min(provider.Length, reset.Index + 300)],
                                      @"this\.droppedEntries = 0;"),
                "a transcript reset keeps the previous conversation's dropped count");
    }

    /// <summary>
    /// <c>/branch</c> is decided on this transcript — the one the fork runs on — through
    /// <c>session/branchCommand</c>, never on the host's history, where questions carry the RAG
    /// auto-context and compaction renumbers turns.
    /// </summary>
    [Fact]
    public void Branch_IsDecidedOnTheDisplayedTranscript()
    {
        var provider = TsCode("chatViewProvider.ts");

        var effect = Regex.Match(provider, @"case 'branchCommand':[\s\S]*?break;");
        Assert.True(effect.Success, "no branchCommand effect: /branch is not decided on the displayed transcript.");
        Assert.Contains("notes.push(", effect.Value, StringComparison.Ordinal);

        var decide = Body(provider, "private async decideBranch(");
        Assert.Contains("this.snapshot()", decide, StringComparison.Ordinal);
        Assert.Contains("host.sessionBranchCommand(", decide, StringComparison.Ordinal);
        Assert.Contains("this.branchAtTurn(", decide, StringComparison.Ordinal);
        Assert.Contains("this.switchToSession(", decide, StringComparison.Ordinal);
        Assert.Contains("ChatViewProvider.errorText(err)", decide, StringComparison.Ordinal);

        Assert.Matches(@"sessionBranchCommand\([\s\S]{0,200}?'session/branchCommand'", TsCode("hostClient.ts"));
    }

    /// <summary>
    /// Loading a session resets the turn counters, as a new conversation does and as the Visual Studio
    /// window does on restore. <c>applySession</c> kept the previous conversation's prompt size, session
    /// tokens and start time: the context gauge showed the old fill, and the export header reported the
    /// old conversation's tokens and a duration counted from its start.
    /// </summary>
    [Fact]
    public void LoadingASession_ResetsTheTurnCounters()
    {
        var provider = TsCode("chatViewProvider.ts");
        var reset    = Body(provider, "async resetConversation(");
        var apply    = Body(provider, "private applySession(");

        foreach (var counter in new[] { "this.promptTokens = 0", "this.lastTokens = 0", "this.sessionTokens = 0", "this.sessionStart = null" })
        {
            Assert.Contains(counter, reset, StringComparison.Ordinal);   // witness: a new conversation resets it
            Assert.True(apply.Contains(counter, StringComparison.Ordinal),
                $"applySession keeps \"{counter}\" from the previous conversation.");
        }
    }

    /// <summary>
    /// A chat message sent while the backend is known to be down is refused before anything is consumed,
    /// as in the Visual Studio window: no user bubble, nothing in the host's history, the text back in
    /// the input box. VS Code sent it anyway — the box was cleared, the question entered the host's
    /// history, and a long prompt was lost. Slash commands, served by the host, still run.
    /// </summary>
    [Fact]
    public void AMessageSentWhileTheBackendIsDown_IsRefused_AndGoesBackToTheInputBox()
    {
        var send  = Body(TsCode("chatViewProvider.ts"), "private async send(");
        var guard = send.IndexOf("this.status?.connected === false", StringComparison.Ordinal);
        var busy  = send.IndexOf("this.busy = true", StringComparison.Ordinal);

        Assert.True(busy >= 0, "send() no longer marks the turn busy: the rule measures nothing.");
        Assert.True(guard >= 0 && guard < busy, "send() consumes the message before checking the backend.");

        var check = send[guard..busy];
        Assert.Contains("await this.pollBackendStatus()", check, StringComparison.Ordinal);
        Assert.Contains("type: 'setPrompt'", check, StringComparison.Ordinal);
        Assert.Contains("startsWith('/')", send[..busy], StringComparison.Ordinal);
    }

    /// <summary>
    /// A turn stopped before any text keeps a lasting cancellation and no empty answer, as in the Visual
    /// Studio window. The adapter saved an empty assistant entry, and "Cancelled" existed only in the
    /// webview: after a reload the session showed a blank answer and nothing about the stop.
    /// </summary>
    [Fact]
    public void ACancelledTurn_KeepsALastingCancellation_AndNoEmptyAnswer()
    {
        var chatTurn = Body(TsCode("chatViewProvider.ts"), "private async chatTurn(");

        var at = chatTurn.IndexOf("else if (result.cancelled)", StringComparison.Ordinal);
        Assert.True(at >= 0, "a cancelled turn is saved like a finished one: an empty answer, no lasting cancellation.");
        var end    = chatTurn.IndexOf("} else {", at, StringComparison.Ordinal);
        var branch = chatTurn[at..(end < 0 ? chatTurn.Length : end)];

        Assert.Contains("vscode.l10n.t('Cancelled.')", branch, StringComparison.Ordinal);
        Assert.Contains("finalText.trim().length > 0", branch, StringComparison.Ordinal);
    }

    /// <summary>
    /// The chips a turn consumed are named under its saved question, as in the Visual Studio window
    /// (ChatTurnPolicy.BuildBubbleText). Only @-mentions stay written in the question; a chip's content
    /// goes to the model alone, so the saved session and the export showed "explain this" with nothing
    /// it referred to.
    /// </summary>
    [Fact]
    public void TheChipsATurnConsumed_AreNamedUnderTheSavedQuestion()
    {
        var source   = TsCode("chatViewProvider.ts");
        var chatTurn = Body(source, "private async chatTurn(");
        Assert.True(chatTurn.Contains("this.nameAttachmentsInQuestion(", StringComparison.Ordinal),
            "the chips are sent to the model and named nowhere the conversation keeps.");

        var naming = Body(source, "private nameAttachmentsInQuestion(");
        Assert.Contains("vscode.l10n.t('📎 Attached: {0}'", naming, StringComparison.Ordinal);
        Assert.Contains("m.role === 'user'", naming, StringComparison.Ordinal);
    }

    /// <summary>
    /// Regenerate takes the previous exchange back — from the view and from the host's history — before
    /// resending the question, as in the Visual Studio window. Resent on top, the old answer stayed in the
    /// conversation and the model read it and the question twice. Reachability is checked before anything
    /// is removed.
    /// </summary>
    [Fact]
    public void Regenerate_TakesTheLastExchangeBack_BeforeResending()
    {
        var source = TsCode("chatViewProvider.ts");
        Assert.Contains("await this.regenerate();", source, StringComparison.Ordinal);

        var regenerate = Body(source, "private async regenerate(");
        var guard    = regenerate.IndexOf("connected === false", StringComparison.Ordinal);
        var rollback = regenerate.IndexOf("host.chatRollbackLastTurn()", StringComparison.Ordinal);
        var splice   = regenerate.IndexOf("this.transcript.splice(", StringComparison.Ordinal);
        var resend   = regenerate.IndexOf("this.send(", StringComparison.Ordinal);

        Assert.True(rollback >= 0 && splice >= 0, "regenerate resends the question on top of the previous exchange.");
        Assert.True(guard >= 0 && guard < rollback, "with the backend down, the exchange is gone before the resend is refused.");
        Assert.True(rollback < resend && splice < resend, "the question is resent before its previous exchange is taken back.");
    }

    /// <summary>
    /// An in-place code action (/fix /refactor /doc) that throws — the workspace edit rejected, the document
    /// closed under it — ends the turn with the error. Released only by the finally, the adapter was idle
    /// while the webview never got turnEnded: the Stop button stayed and the failure was said nowhere.
    /// </summary>
    [Fact]
    public void AThrowingCodeAction_StillEndsTheTurn()
    {
        var send = Body(TsCode("chatViewProvider.ts"), "private async send(");
        var at   = send.IndexOf("await this.runCodeAction(", StringComparison.Ordinal);
        Assert.True(at >= 0, "the code-action branch of send() moved — the rule measures nothing.");

        var end   = send.IndexOf("finally", at, StringComparison.Ordinal);
        var block = send[at..(end < 0 ? send.Length : end)];
        Assert.True(block.Contains("catch", StringComparison.Ordinal) && block.Contains("this.finishTurn(", StringComparison.Ordinal),
            "a code action that throws leaves the webview busy: no turnEnded, no error shown.");
    }

    /// <summary>
    /// Copying a message's text and opening an approval's diff reach VS Code APIs that can refuse (a clipboard a
    /// remote session denies, a document that cannot be opened). Unguarded, the click did nothing and nothing said
    /// why — for the diff, at the very moment the user wants to read what they are about to allow.
    /// </summary>
    [Theory]
    [InlineData("copyText")]
    [InlineData("openApprovalDiff")]
    [InlineData("attachBrowse")]
    public void AGestureThatReachesVsCodeApis_SaysWhenItFails(string message)
    {
        // The whole source, not Body(): the signature of onMessage carries a `{` in its parameter type.
        var source = TsCode("chatViewProvider.ts");
        var at     = source.IndexOf($"case '{message}':", StringComparison.Ordinal);
        Assert.True(at >= 0, $"case '{message}' moved — the rule measures nothing.");

        var next  = source.IndexOf("case '", at + 6, StringComparison.Ordinal);
        var block = source[at..(next < 0 ? source.Length : next)];
        Assert.True(block.Contains("catch", StringComparison.Ordinal)
                    && block.Contains("showWarningMessage(", StringComparison.Ordinal),
            $"'{message}' fails in silence when the VS Code API refuses.");
    }

    /// <summary>
    /// Reasoning models are not consistent about the case of their tags: Visual Studio strips <c>&lt;THINK&gt;</c> and
    /// <c>&lt;Think&gt;</c> (MarkdownParser, IgnoreCase), the webview stripped lowercase only — and with raw HTML
    /// disabled, the model's chain of thought was shown in the VS Code bubble. The webview's own patterns run here.
    /// </summary>
    [Theory]
    [InlineData("<think>a</think>garde<think>b</think>", "garde")]
    [InlineData("<think>\nligne 1\nligne 2\n</think>visible", "visible")]
    [InlineData("<THINK>bruit</THINK>visible", "visible")]
    [InlineData("<Think>bruit</Think>visible", "visible")]
    [InlineData("visible<Think>still streaming", "visible")]
    public void TheWebviewStripsReasoning_TheWayVisualStudioDoes(string content, string expected)
    {
        var body     = Body(TsCode("webview/markdown.ts"), "export function stripThinkTags(");
        var literals = System.Text.RegularExpressions.Regex.Matches(body, @"\.replace\(/((?:\\/|[^/\n])+)/([a-z]*),");
        Assert.True(literals.Count == 2, $"{literals.Count} patterns read in stripThinkTags — the rule measures nothing.");

        var result = content;
        foreach (System.Text.RegularExpressions.Match m in literals)
        {
            var flags   = m.Groups[2].Value;
            var options = flags.Contains('i') ? System.Text.RegularExpressions.RegexOptions.IgnoreCase
                                              : System.Text.RegularExpressions.RegexOptions.None;
            var regex   = new System.Text.RegularExpressions.Regex(m.Groups[1].Value.Replace(@"\/", "/"), options);
            // JavaScript replaces every match only with the 'g' flag.
            result = flags.Contains('g') ? regex.Replace(result, "") : regex.Replace(result, "", 1);
        }

        Assert.Equal(expected, result.Trim());
    }

    /// <summary>
    /// A streamed answer keeps the model's inline reasoning in its text — the bubble strips it when it renders. The copy
    /// button of an answer put that hidden reasoning on the clipboard. It copies what the bubble shows.
    /// </summary>
    [Fact]
    public void CopyingAnAnswer_LeavesTheModelsHiddenReasoningOut()
    {
        var calls = System.Text.RegularExpressions.Regex.Matches(TsCode("webview/main.ts"), @"metaRow\((?<args>[^;]*)\);");
        Assert.True(calls.Count >= 2, $"{calls.Count} metaRow calls read — the rule measures nothing.");
        foreach (System.Text.RegularExpressions.Match call in calls)
            Assert.True(call.Groups["args"].Value.Contains("stripThinkTags(", StringComparison.Ordinal),
                $"metaRow({call.Groups["args"].Value}) copies the raw text, the model's hidden reasoning included.");
    }

    /// <summary>
    /// An @-mention the user picked reads the clipboard, the Problems panel or asks the host: each can refuse, and
    /// the chip that never appears reads as a broken feature. The failure is said, not only logged.
    /// </summary>
    [Fact]
    public void AMentionThatCannotBeResolved_SaysSo()
    {
        var source = TsCode("chatViewProvider.ts");
        var at     = source.IndexOf("private async resolveMention(", StringComparison.Ordinal);
        var end    = source.IndexOf("private async openXray(", StringComparison.Ordinal);
        Assert.True(at >= 0 && end > at, "resolveMention or its neighbour moved — the rule measures nothing.");

        var body = source[at..end];
        Assert.Contains("clipboard.readText()", body);
        Assert.True(body.Contains("showWarningMessage(", StringComparison.Ordinal),
            "A mention that fails to resolve leaves no chip and no word.");
    }

    /// <summary>
    /// The webview's messages are dispatched by a promise nobody awaited: a handler that throws past its own guards
    /// became an unhandled rejection, invisible in the Inferpal output channel. The dispatch owns a last catch.
    /// </summary>
    [Fact]
    public void TheWebviewDispatch_CatchesWhatAHandlerLetsThrough()
    {
        var source = TsCode("chatViewProvider.ts");
        Assert.Contains("onDidReceiveMessage(", source);
        Assert.Matches(@"onDidReceiveMessage\(\s*\(msg: WebviewToExt\)\s*=>\s*[\s\S]{0,40}this\.onMessage\(msg\)\s*\.catch\(", source);
    }

    /// <summary>
    /// A file can be pinned from the chat, as in the Visual Studio window: the "+" menu pins the active file, the
    /// pinned files show above the composer and each can be removed there. VS Code could only pin through the
    /// settings panel, a pin nobody saw from the chat.
    /// </summary>
    [Fact]
    public void AFile_CanBePinnedFromTheChat_AndUnpinnedThere()
    {
        var webview = TsCode("webview/main.ts");
        Assert.Contains("type: 'pinActive'", webview, StringComparison.Ordinal);
        Assert.Contains("type: 'unpin'", webview, StringComparison.Ordinal);

        var provider = TsCode("chatViewProvider.ts");
        var pin      = provider.IndexOf("case 'pinActive':", StringComparison.Ordinal);
        var unpin    = provider.IndexOf("case 'unpin':", StringComparison.Ordinal);
        Assert.True(pin >= 0 && unpin >= 0, "the chat has no pin or unpin gesture.");
        Assert.Contains("pinsAdd(", provider[pin..(provider.IndexOf("case '", pin + 6, StringComparison.Ordinal))], StringComparison.Ordinal);
        Assert.Contains("pinsRemove(", provider[unpin..(provider.IndexOf("case '", unpin + 6, StringComparison.Ordinal))], StringComparison.Ordinal);
    }

    /// <summary>
    /// The agent mode starts the same in both editors. Visual Studio starts in Chat (with tools), VS Code started
    /// in Agent: the same question ran the orchestrator in one and the chat loop in the other. The extension's
    /// code fallbacks follow the declared default.
    /// </summary>
    [Fact]
    public void TheAgentModeDefault_IsTheSameInBothEditors()
    {
        var manifest = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "vscode", "package.json"))).RootElement;
        var configuration = manifest.GetProperty("contributes").GetProperty("configuration");
        var sections = configuration.ValueKind == System.Text.Json.JsonValueKind.Array
            ? configuration.EnumerateArray().ToList()
            : [configuration];
        var declared = sections
            .Select(s => s.TryGetProperty("properties", out var p) && p.TryGetProperty("inferpal.agentMode", out var m) ? m : (System.Text.Json.JsonElement?)null)
            .First(m => m is not null)!.Value.GetProperty("default").GetBoolean();

        var visualStudio = new Inferpal.Config.InferpalConfig().AgentModeEnabled;
        Assert.Equal(visualStudio, declared);

        var fallbacks = System.Text.RegularExpressions.Regex.Matches(
            TsCode("chatViewProvider.ts"), @"get<boolean>\('agentMode', (true|false)\)");
        Assert.NotEmpty(fallbacks);   // witness: the extension still reads the setting with a fallback
        Assert.All(fallbacks, f => Assert.Equal(visualStudio ? "true" : "false", f.Groups[1].Value));
    }

    /// <summary>
    /// /explain and /review read the editor after the turn has started: a throw there left the provider busy
    /// for good — every later message and regenerate was dropped by the busy guard, with the Stop button up.
    /// </summary>
    [Fact]
    public void AThrowingExplainOrReview_StillEndsTheTurn()
    {
        var send = Body(TsCode("chatViewProvider.ts"), "private async send(");
        var at   = send.IndexOf("await this.runExplainReview(", StringComparison.Ordinal);
        Assert.True(at >= 0, "the /explain and /review branch of send() moved — the rule measures nothing.");

        var end   = send.IndexOf("return;", at, StringComparison.Ordinal);
        var block = send[at..(end < 0 ? send.Length : end)];
        Assert.True(block.Contains("catch", StringComparison.Ordinal) && block.Contains("this.finishTurn(", StringComparison.Ordinal),
            "an /explain or /review that throws leaves the provider busy: every later message is dropped.");
    }

    /// <summary>
    /// Picking a model or switching the agent mode writes the workspace settings, which can refuse (a
    /// settings.json with a syntax error, a read-only file). The refusal stopped the gesture half-way in
    /// silence: the host kept the previous model, the agent switch did not move.
    /// </summary>
    [Theory]
    [InlineData("pickModel")]
    [InlineData("toggleAgentMode")]
    public void ASettingTheWorkspaceRefuses_IsSaid(string message)
    {
        // The whole source, not Body(): the signature of onMessage carries a `{` in its parameter type.
        var onMessage = TsCode("chatViewProvider.ts");
        var at        = onMessage.IndexOf($"case '{message}':", StringComparison.Ordinal);
        Assert.True(at >= 0, $"case '{message}' moved — the rule measures nothing.");

        var next  = onMessage.IndexOf("case '", at + 6, StringComparison.Ordinal);
        var block = onMessage[at..(next < 0 ? onMessage.Length : next)];
        Assert.Contains(".update(", block, StringComparison.Ordinal);   // witness: the case still writes the setting
        Assert.True(block.Contains("catch", StringComparison.Ordinal)
                    && block.Contains("vscode.l10n.t('Inferpal could not save this setting: {0}'", StringComparison.Ordinal),
            $"'{message}' stops in silence when the workspace settings refuse the write.");
    }
}
