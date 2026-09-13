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
}
