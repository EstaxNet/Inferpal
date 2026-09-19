using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Commands;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Docs;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.Extensibility.Settings;
using Microsoft.VisualStudio.Extensibility.UI;
using Microsoft.VisualStudio.Threading;

namespace Inferpal.ToolWindow;

internal partial class InferpalToolWindowData
{
    #region Pending prompt, active file & sessions

    // ── Pending prompt from editor context menu ────────────────────────────────

    private void OnPendingPromptAvailable(object? sender, EventArgs e) => ConsumePendingPrompt();

    private void OnActiveFileChanged(object? sender, string filePath)
    {
        _activeFilePath = filePath;

        // Rebuild the system prompt when either persona auto-switching is on (existing behaviour)
        // or glob-scoped project rules exist — both depend on the active file. If neither applies,
        // the prompt is identical, so skip the rebuild.
        var language = _config.PersonaAutoSwitch ? DetectLanguage(filePath) : null;
        if (string.IsNullOrEmpty(language) && !HasGlobScopedRules()) return;

        // The persona lives in a field that BuildSystemPrompt reads: passed as an argument, it lasted
        // until the next rebuild (plan toggle, /note, X-Ray…), which dropped it without a word.
        Post(() =>
        {
            if (!string.IsNullOrEmpty(language)) _personaLanguage = language;
            RefreshSystemPrompt();
        });
    }

    // True if any project rule is glob-scoped (not alwaysApply / not unscoped), meaning the
    // injected rule set can change with the active file and the prompt must be rebuilt on switch.
    private bool HasGlobScopedRules()
    {
        var dir = FindProjectRoot();
        var rulesDir = Path.Combine(dir, ".inferpal", "rules");
        return RulesService.Load(rulesDir).Any(r => !r.AlwaysApply && r.Globs.Count > 0);
    }

    private void ConsumePendingPrompt()
    {
        // One step: the prompt and its model/attachment cannot come from two different context-menu actions.
        var pending = _contextHolder.ConsumePending();
        if (pending is null) return;
        var p           = pending.Prompt;
        var m           = pending.Model;
        var attachLabel = pending.AttachLabel;
        var attachCode  = pending.AttachContent;

        List<AttachmentItem> atts = [];
        if (!string.IsNullOrEmpty(attachLabel) && !string.IsNullOrEmpty(attachCode))
            atts.Add(new AttachmentItem(attachLabel, attachCode, () => {}));

        // Auto-send the code action directly on the VM context.
        // The model is captured in the closure — no volatile field or
        // cross-thread race condition possible.
        // clearPrompt: false so the user's current draft is preserved.
        // Cancel any in-flight request on the VM context before starting — and WAIT for it to
        // unwind: firing the next turn immediately lets the cancelled turn's finally stomp
        // IsLoading/_currentCts under the new one (dead stop button, a third send possible,
        // interleaved bubbles). _turnDone is completed by the owning turn's conditional
        // finalisation.
        Post(() =>
        {
            var previousTurn = _turnDone?.Task;
            _currentCts?.Cancel();
            _ = RunPendingTurnAsync(previousTurn, p, m, atts);
        });
    }

    private async Task RunPendingTurnAsync(Task? previousTurn, string p, string? m, List<AttachmentItem> atts)
    {
        try
        {
            // Belt over the signal: a turn that never completes its finalisation must not make
            // code actions permanently dead — 15 s is far beyond any real unwind.
            // VSTHRD003 is a false positive here: previousTurn is a TCS completed on the VM's
            // NonConcurrentSynchronizationContext (RunContinuationsAsynchronously) in this OOP
            // process — no JTF main thread exists to deadlock against.
#pragma warning disable VSTHRD003
            if (previousTurn is not null)
                await Task.WhenAny(previousTurn, Task.Delay(TimeSpan.FromSeconds(15)));
            // The session restored at window opening replaces the conversation: a code action that
            // opened the window must not start its turn underneath it. LoadSessionAsync never throws.
            await _startupSessionLoad;
#pragma warning restore VSTHRD003
            // Alt+M posts "/map": a slash command goes to the router, never to the model as chat text.
            if (p.StartsWith('/'))
            {
                PinWorkspaceRoot();
                await HandleSlashCommandAsync(p, CancellationToken.None);
            }
            else
                await SendCoreAsync(p, m, atts, CancellationToken.None, clearPrompt: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Swallow("PendingPrompt.Run", ex); }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task SaveNamedSessionAsync(string firstUserContent, List<SavedMessage> snapshot)
    {
        try
        {
            var title = await GenerateSessionTitleAsync(firstUserContent);
            var name  = SessionManager.UniqueSessionName(
                SessionManager.SessionFileName(DateTime.Now, title), _store.ListSessions());
            await _store.SaveAsync(name, snapshot, CancellationToken.None);
            await RunOnVMContextAsync(RefreshSessionsList);
        }
        catch (Exception ex)
        {
            // ⚠ This runs fire-and-forget — /clear must not wait on the model call that names the
            // session — so by the time it fails the transcript is ALREADY cleared: the failure lands
            // after the only moment the user could still have copied anything. A trace in
            // /diagnostics is therefore not a channel; the conversation they are now in is. The same
            // rule is applied two methods down for a session that could not be deleted.
            Diagnostics.Swallow("Session.SaveNamed", ex);
            var reason = ex.Message;
            await RunOnVMContextAsync(() =>
                InsertThemed(ChatMessageItem.AssistantMsg(Strings.SessionArchiveFailed(reason))));
        }
    }

    // Shared with the Host (`session/title`, VS Code) — prompt, timeout and fallback live in the
    // Core so the two front-ends can never drift apart on how a session gets named.
    private Task<string> GenerateSessionTitleAsync(string firstUserContent) =>
        SessionTitleGenerator.GenerateAsync(_client, _config, firstUserContent, CancellationToken.None);

    /// <summary>Set when the auto-save last failed and the user was told; cleared by the next one
    /// that works.</summary>
    private bool _autoSaveFailureTold;

    private async Task AutoSaveAsync()
    {
        try
        {
            List<SavedMessage> snapshot = [];
            await RunOnVMContextAsync(() =>
            {
                snapshot = SessionManager.BuildSnapshot(
                    Messages.Select(m => (m.Role, m.Content, m.ToolName, m.Timestamp)));
            });
            await _store.AutoSaveAsync(snapshot, CancellationToken.None, _indexService.RootDir);
            _autoSaveFailureTold = false;
        }
        catch (Exception ex)
        {
            // ⚠ The product PROMISES this conversation comes back — it restores it at window
            // opening — so an auto-save that never works loses it at the next close, with nothing
            // said. The transcript is still on screen here (unlike the archive of a /clear two
            // methods up), so the notice names what to do while it still can.
            Diagnostics.Swallow("Session.AutoSave", ex);
            // Said ONCE: this runs on every turn and its causes last. Re-armed by the next save
            // that works, otherwise "once" becomes "once in the life of the window".
            if (_autoSaveFailureTold) return;
            _autoSaveFailureTold = true;
            var reason = ex.Message;
            await RunOnVMContextAsync(() =>
                InsertThemed(ChatMessageItem.AssistantMsg(Strings.SessionAutoSaveFailed(reason))));
        }
    }

    private async Task DeleteSessionAsync(object? _, CancellationToken ct)
    {
        var name = SelectedSession;
        if (string.IsNullOrEmpty(name)) return;

        var confirmed = await _vs.Shell().ShowPromptAsync(
            Strings.DeleteSessionConfirm(name), PromptOptions.OKCancel, ct);
        if (!confirmed) return;

        try
        {
            _store.Delete(name);
        }
        catch (Exception ex)
        {
            // A locked or read-only session file: confirmed, and nothing happened, with nothing said.
            Diagnostics.Swallow("Session.Delete", ex);
            var message = ex.Message;
            await RunOnVMContextAsync(() => InsertThemed(ChatMessageItem.AssistantMsg(Strings.MsgError(message))));
            return;
        }
        await RunOnVMContextAsync(() =>
        {
            // BEFORE the refresh: a selected session is held, hence kept in the list — the deleted
            // one would stay there.
            SelectedSession = string.Empty;
            RefreshSessionsList();
        });
    }

    /// <remarks>
    /// ⚠ In place, never removing the selected session. This refresh also runs in the background
    /// (after a <c>/clear</c> is archived, once its title is generated): clearing the list reset
    /// <see cref="SelectedSession"/> to null under the user's eyes, and "Load" then opened
    /// <c>last_session</c> instead of the chosen session — or nothing.
    /// </remarks>
    private void RefreshSessionsList() =>
        SelectionPreservingList.Sync(RecentSessions,
            _store.ListSessions().Where(s => s != "last_session").ToList(), [SelectedSession]);

    // ── 30-fps UI throttle ─────────────────────────────────────────────────────

    // Batches incoming tokens and flushes them to the UI at ~30 fps (32 ms).
    // Prevents flooding the UI thread when the model streams tokens rapidly.
    #endregion
}
