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
    #region Connection, heartbeat, sessions & modes

    private async Task RetryConnectionAsync(object? _, CancellationToken ct)
    {
        _client.ResetCircuit();
        // Dispose the old CTS and hand the NEW token to the loop explicitly: a loop that reads
        // _heartbeatCts.Token at ITS start shares it with the next one, so a double-click starts
        // two loops on the same token — double polling and doubled "connection restored" bubbles.
        // Captured here, each loop dies with its own token.
        var old = _heartbeatCts;
        _heartbeatCts = new CancellationTokenSource();
        var token = _heartbeatCts.Token;
        try { await old.CancelAsync(); } finally { old.Dispose(); }
        // Keep _isBackendReachable=false until the next heartbeat tick confirms success —
        // this prevents a race where the user clicks Send during the 2-second check delay.
        await RunOnVMContextAsync(() =>
        {
            ShowRetryButton       = false;
            _connected            = null;   // the header says "checking" until the next heartbeat answers
            RefreshModelButton();
        });
        _ = StartHeartbeatAsync(token);
    }

    // ── VRAM monitoring ───────────────────────────────────────────────────────

    /// <summary>
    /// Called on a thread-pool thread by <see cref="ModelLifetimeService"/> whenever
    /// the running-model list is refreshed.  Formats a compact VRAM badge string and
    /// marshals the update to the Remote UI synchronization context.
    /// </summary>
    private void OnModelsRefreshed(IReadOnlyList<RunningModelInfo> models)
    {
        var text = Services.Inference.ModelCatalog.FormatVramBadge(models);

        SynchronizationContext.Post(_ =>
        {
            try
            {
                VramStatus    = text;
                HasVramStatus = text.Length > 0;
            }
            catch (Exception ex) { Diagnostics.Swallow("Connection.VramBadgeUpdate", ex); }
        }, null);
    }

    /// <summary>
    /// The backend answers: an unfinished first run completes, otherwise a default model nobody chose that the backend
    /// does not have is replaced by the best installed one, and said (<see cref="ModelCatalog.FirstModelToAdopt(InferpalConfig, ModelsByKind)"/>)
    /// — then a context window never sized is sized for the model that answers (<see cref="ContextWindowChoice.CatchUpAsync"/>).
    /// </summary>
    private async Task EnsureChatModelAsync()
    {
        if (_config.IsFirstRun)
        {
            await StartFirstRunDiscoveryAsync().ConfigureAwait(false);
            return;
        }
        try
        {
            // A model chosen in another window since this one read the settings is that choice, not a default to replace.
            if (_config.FillFromFile(nameof(InferpalConfig.DefaultModel)))
                await RunOnVMContextAsync(() => ActiveModelLabel = _config.DefaultModel).ConfigureAwait(false);
            else
            {
                var listed = await _client.ListModelsByKindAsync(CancellationToken.None).ConfigureAwait(false);
                if (ModelCatalog.FirstModelToAdopt(_config, listed) is { } adopted)
                {
                    var configured = _config.DefaultModel;
                    _config.DefaultModel = adopted;
                    _config.Save();
                    await RunOnVMContextAsync(() => ActiveModelLabel = adopted).ConfigureAwait(false);
                    await FirstRunPresentAsync(Strings.MsgModelAdopted(configured, adopted)).ConfigureAwait(false);
                }
                // A chosen model the server does not have: said now, once per connection, not by the question's failure.
                else if (ModelCatalog.NotListedNotice(_config.DefaultModel, listed) is { } missing)
                    await FirstRunPresentAsync(missing).ConfigureAwait(false);
            }
            if (await ContextWindowChoice.CatchUpAsync(_config, _client, _config.DefaultModel, CancellationToken.None).ConfigureAwait(false)
                is { } windowNotice)
                await FirstRunPresentAsync(windowNotice).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Swallow("Connection.AdoptModel", ex); }
    }

    private bool _settingsUnreadableSaid;

    /// <summary>
    /// The settings another editor saved since this window took them (<see cref="Services.Inference.SettingsFollow"/>):
    /// taken as the file's, applied as a save applies them (<see cref="OnSettingsFollowed"/>); what this window changed
    /// itself and did not save stays, unless the file changed that same setting. A new kind of model server is taken like
    /// the rest, and reaches the client at the door (<see cref="FollowServer"/>).
    /// </summary>
    private async Task FollowSettingsFileAsync()
    {
        try
        {
            var follow = Services.Inference.SettingsFollow.TakeAndApply(_config, _client, () => GpuScheduler.IsTurnInFlight);
            // Before the question leaves: it goes to the server the settings name now (a new kind included), checked
            // here — the heartbeat's last answer was the old server's.
            if (!follow.Busy && FollowServer())
                _isBackendReachable = await _client.CheckConnectionAsync(_client.ServerAddress, CancellationToken.None);
            // Said once while it lasts: the person who just edited the file would see their change do nothing, without a word.
            if (follow.Unreadable && !_settingsUnreadableSaid) await ShowInfoAsync(Strings.AcpSettingsUnreadable);
            if (!follow.Busy) _settingsUnreadableSaid = follow.Unreadable;
            // The question answers with the MCP servers the other editor saved, and the ones the repository declares now
            // (a checkout changed its .mcp.json): started again before it, not after.
            // ⚠ Never under a running turn: a refresh tears every server down, and a slash command (/resume during a
            // step pause) runs this mid-run — then it waits for the turn to end (RefreshWhenIdleAsync says why).
            if (follow.Taken.Contains("mcpEnabled") || follow.Taken.Contains("mcpServersJson")
                || _tools.Mcp.RepositoryFilesChanged())
            {
                if (GpuScheduler.IsTurnInFlight)
                    _ = _tools.Mcp.RefreshWhenIdleAsync(() => GpuScheduler.IsTurnInFlight, TimeSpan.FromMilliseconds(500));
                else
                    await RefreshMcpAsync();
            }
        }
        catch (Exception ex) { Diagnostics.Swallow("Settings.Follow", ex); }
    }

    /// <summary>Settings another editor saved, now this window's: the language — the
    /// rest follows <see cref="InferpalConfig.Saved"/>, as after a save in this window. The MCP servers are started again
    /// by the gesture that followed (a question before its turn, the settings window once no turn runs): started here
    /// too, they would be torn down twice, the second time under the turn.</summary>
    private void OnSettingsFollowed(IReadOnlyList<string> taken, string? newKind)
    {
        if (taken.Contains("language"))
        {
            Strings.ApplyLanguage(_config.Language);
            _config.NotifyLanguageChanged();
        }
        _ = newKind;   // Visual Studio's client takes a new kind itself (SwitchingProvider): nothing waits for a restart
    }

    /// <summary>
    /// The turn door of the chat's client (<see cref="SwitchingProvider.Follow"/>): the settings as they are now — a new
    /// kind of server, a new address — reach it, unless a turn of this process runs (<see cref="TurnDoor"/>: a long
    /// command between two model calls, a <c>/task</c> run, a turn waiting at a card), which keeps the server it started
    /// with; the heartbeat takes them once it ends. Returns whether the chat's server changed.
    /// </summary>
    private bool FollowServer()
    {
        if (_client is not SwitchingProvider switching) return false;
        var changed = false;
        TurnDoor.TryNow(() => changed = switching.Follow());
        return changed;
    }

    /// <summary>
    /// <c>/setup</c>'s own door: it runs as a turn of this window, but its purpose is to change the server and it asks the
    /// server through a client of its own — only a model call in flight (a <c>/task</c> run) keeps the old one. Called
    /// BEFORE the save: <see cref="InferpalConfig.Saved"/> measures the context window through the chat's client.
    /// </summary>
    private void FollowServerForSetup()
    {
        if (!GpuScheduler.IsTurnInFlight) (_client as SwitchingProvider)?.Follow();
    }

    /// <summary>What a gesture refused for an unreachable backend says: the client's own server, the one the turn would
    /// go to. One writer for every refusal (send, Regenerate); the heartbeat says its edge itself.</summary>
    private string UnreachableText() =>
        Strings.MsgConnectionLost(_client.ServerAddress, InferenceProviderFactory.DisplayName(ServerCode), _client.ConnectionRefusal);

    /// <summary>The kind of server the client speaks to (<see cref="SwitchingProvider.Kind"/>): under a turn, the one it
    /// started with, whatever was saved since.</summary>
    private string ServerCode => InferenceProviderFactory.CodeOf(_client) ?? _config.Provider;

    /// <summary>Ends the heartbeat of a window run by a test: left going, it follows the folder the next test opens. A turn
    /// the test left open gives the door back too (<see cref="TurnDoor"/> is the process's): held, it would keep every
    /// later test's server from following.</summary>
    internal void StopHeartbeatForTests()
    {
        _heartbeatCts.Cancel();
        _doorHold?.Dispose();
    }

    private async Task StartHeartbeatAsync(CancellationToken? token = null)
    {
        var ct = token ?? _heartbeatCts.Token;
        try
        {
            // Let the window finish its initial render before the first check.
            await Task.Delay(2_000, ct);

            var presenter    = new ConnectionStatusPresenter();
            var modelChecked = false;

            while (!ct.IsCancellationRequested)
            {
                // Pin the workspace root (RAG on or off) and follow solution switches.
                PinWorkspaceRoot();
                await RestoreLateSlotAsync(ct);
                await ShowAwayNoticeAsync();

                // The door, when no turn runs: settings changed by a path that did not pass it reach the client here.
                FollowServer();
                // ⚠ The CLIENT's server, not the live settings': under a turn it keeps the one it started with.
                var url = _client.ServerAddress;
                var ok  = await _client.CheckConnectionAsync(url, ct);

                _isBackendReachable = ok; // volatile write — read by SendCoreAsync pre-flight

                // A first run says what answers where: before it, the sentence below told someone with LM Studio to start
                // Ollama. Waited for, so that one fact decides; what it adopted or read from the file is read after it.
                var firstRunSpoke = !ok && await FirstRunSpokeAsync(ct);
                if (!ok) url = _client.ServerAddress;

                var status = presenter.Evaluate(ok, _client.ConnectionRefusal, firstRunSpoke);

                await RunOnVMContextAsync(() =>
                {
                    SendButtonColor       = status.SendButtonColor;
                    ShowRetryButton       = status.ShowRetry;
                    _connected            = ok;
                    _refusal              = ok ? null : _client.ConnectionRefusal;
                    RefreshModelButton();

                    var edgeMessage = status.Transition switch
                    {
                        ConnectionTransition.Restored => Strings.MsgHeartbeatRestored(InferenceProviderFactory.DisplayName(ServerCode)),
                        ConnectionTransition.Lost     => Strings.MsgConnectionLost(
                                                             url, InferenceProviderFactory.DisplayName(ServerCode), _client.ConnectionRefusal),
                        _                             => null,
                    };
                    if (edgeMessage is not null)
                    {
                        var msg = ChatMessageItem.NoticeMsg(edgeMessage);
                        ApplyItemTheme(msg);
                        Messages.Insert(Messages.Count - 2, msg);
                        ScrollToBottom();
                    }
                });

                // The first check that reaches the backend, and every return of it: a first run that could not choose
                // a model completes, and a default nobody chose that the backend lacks is replaced.
                if (ok && (!modelChecked || status.Transition == ConnectionTransition.Restored))
                {
                    modelChecked = true;
                    _ = EnsureChatModelAsync();
                }

                // Connected: poll every 20 s (was 60 s) — fast enough to catch a Ollama crash
                // within one poll cycle without hammering the daemon.
                // Disconnected: poll every 8 s (was 15 s) — show recovery promptly.
                await Task.Delay(ok ? 20_000 : 8_000, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Swallow("Connection.Heartbeat", ex); }
    }

    private Task ToggleSessionPanelAsync(object? _, CancellationToken ct) =>
        RunOnVMContextAsync(() => IsSessionPanelOpen = !IsSessionPanelOpen);

    private async Task ClearAsync(object? _, CancellationToken ct)
    {
        // A turn in flight owns _history and the streaming bubble: let it unwind before the
        // conversation is replaced under it (see SettleCurrentTurnAsync).
        await SettleCurrentTurnAsync();
        // Behind the auto-saves already started: one still writing would put the discarded conversation back in the slot.
        await _autoSaveGate.WaitAsync(CancellationToken.None);
        try { await ClearUnderSaveGateAsync(ct); }
        finally { _autoSaveGate.Release(); }
    }

    private async Task ClearUnderSaveGateAsync(CancellationToken ct)
    {
        // A turn sent while the gate was awaited is let unwind too: the conversation is replaced under nothing.
        await SettleCurrentTurnAsync();

        // Capture snapshot and first user message on the VM context before clearing.
        bool hasMessages = false;
        string firstUserContent = string.Empty;
        List<SavedMessage> snapshot = [];
        SessionState? archived = null;
        string? root = null;
        var inASlot = false;

        await RunOnVMContextAsync(() =>
        {
            // The workspace of the conversation being discarded: its slot is the one to empty, its folder the archive's.
            // Never saved, it is in no slot — and the one of the solution open now holds that solution's conversation.
            inASlot     = _conversationRoot.IsBound;
            root        = _conversationRoot.For(_indexService.RootDir);
            // Decided on the snapshot, never on Messages: the two scroll anchors are always there,
            // so counting them archived an empty, titled session on every /clear and /template.
            snapshot    = SessionManager.BuildSnapshot(
                Messages.Select(m => m.Saved));
            hasMessages = snapshot.Count > 0;
            if (!hasMessages) return;
            archived         = CaptureState() with { CurrentName = null };
            firstUserContent = ConversationArchive.FirstQuestion(snapshot);
        });

        // ⚠ Archived FIRST, under its first question's words: on disk before the slot is emptied, the model's title only
        // renames it, in the background (ConversationArchive). Archived after the title, VS closed in those seconds lost
        // a conversation that was in no slot and under no name.
        string? archiveFailed = null;
        if (hasMessages)
        {
            var at = DateTime.Now;
            try
            {
                var name = ConversationArchive.FirstName(_store, at, firstUserContent);
                await _store.SaveAsync(name, snapshot, CancellationToken.None, root, state: archived);
                _ = RetitleArchiveAsync(name, at, firstUserContent);
            }
            catch (Exception ex)
            {
                Diagnostics.Swallow("Session.Archive", ex);
                archiveFailed = Strings.SessionArchiveFailedSlotKept(ex.Message);
            }
        }

        // The conversation just discarded leaves the auto-save slot once it is archived — never before: until then the
        // slot is the one place it is kept. Said when it cannot.
        string? notForgotten = null;
        try { if (inASlot && archiveFailed is null) notForgotten = await _store.ForgetAutoSaveOrSayAsync(root, ct); }
        catch (OperationCanceledException) { }

        await RunOnVMContextAsync(() =>
        {
            Messages.Clear();
            Messages.Add(_anchor0);
            Messages.Add(_anchor1);
            _activeTemplateSuffix     = string.Empty;
            _threadReloaded           = false;
            _workspaceContextInjected = false;
            _sessionStartTime         = null;
            _lastRegenerableMsg       = null;
            _lastContinuableMsg       = null;
            _modeModelNoticeSaid      = null;   // a new conversation is told again when its mode's models are missing
            _baseSystemPrompt         = BuildSystemPrompt();
            _history               = [new("system", _baseSystemPrompt)];
            _oodaSummary           = string.Empty;
            _recapInSystem         = string.Empty;
            _summary               = null;
            _conversationModel     = null;
            _conversationTurnCount = 0;
            _currentSessionName    = string.Empty;   // the archived conversation keeps its own file
            _conversationRoot.Discarded();           // the next conversation belongs where it is first saved
            _rootlessRestore = null;                 // and is no conversation restored without a workspace
            ResetTurnAccounting();
            HasBuildFailedBanner   = false;   // dismiss banner on /clear
            RefreshSessionsList();            // the archive just written
            // After the clear, or it would be cleared with the rest.
            if (archiveFailed is not null) InsertThemed(ChatMessageItem.NoticeMsg(archiveFailed));
            if (notForgotten is not null) InsertThemed(ChatMessageItem.NoticeMsg(notForgotten));
        });
    }

    /// <summary>The root a late restore was already tried for (<see cref="RestoreLateSlotAsync"/>): once per root.</summary>
    private string? _lateRestoreTried;

    /// <summary>The conversation restored at a start without a workspace, as it stood then (<see cref="LoadSessionAsync"/>):
    /// what a solution opened later replaces with its own while nothing was done since; <c>null</c> otherwise.</summary>
    private RootlessRestore? _rootlessRestore;

    /// <param name="State">The conversation's state at the restore (<see cref="CaptureState"/>).</param>
    /// <param name="Turns">What a save would write of it then, notices aside (<see cref="SavedTurns"/>).</param>
    /// <param name="Items">The bubbles it put on screen, and the notices said about it since: never kept over the
    /// solution's conversation.</param>
    private sealed record RootlessRestore(SessionState State, IReadOnlyList<(string Role, string Content, string? Tool)> Turns,
                                          HashSet<ChatMessageItem> Items);

    /// <summary>Whether the conversation on screen was restored at a start without a workspace, and belongs to none — never
    /// one begun in this window before its solution, which the person wrote here.</summary>
    private bool RootlessOnScreen => _rootlessRestore is not null && _conversationRoot.IsBound && _conversationRoot.Root is null;

    /// <summary>What a save would write of the conversation, notices aside. VM context only.</summary>
    private List<(string Role, string Content, string? Tool)> SavedTurns() =>
        [.. SessionManager.BuildSnapshot(Messages.Select(m => m.Saved))
                          .Where(m => m.ToolName != SessionManager.NoticeMarker)
                          .Select(m => (m.Role, m.Content, m.ToolName))];

    /// <summary>
    /// Whether nothing was done to the conversation restored without a workspace since its restore: no question sent or
    /// on its way, no answer added (notices aside), no mode, plan or session changed, not discarded. VM context only.
    /// </summary>
    private bool UntouchedSinceRootlessRestore() =>
        _rootlessRestore is { } restore && RootlessOnScreen && !IsLoading && !_sendStarting
        && CaptureState() == restore.State && SavedTurns().SequenceEqual(restore.Turns);

    /// <summary>
    /// ⚠ A window built before its solution is known restores nothing (no root yet, <see cref="LoadSessionAsync"/>). Once
    /// the heartbeat pins the root, the solution's own conversation is restored — while nothing was asked in this window.
    /// Without it, yesterday's conversation stayed unseen, and the first question's save replaced it.
    /// </summary>
    /// <remarks>
    /// ⚠ A conversation restored at a start WITHOUT a workspace (yesterday's, maybe of another project) is no conversation
    /// of the solution opened next: kept on screen without a word, the model took it for the solution's context. It is
    /// replaced by the solution's own while nothing was done since its restore, and said otherwise — at every root it meets.
    /// Never throws but for a cancellation: it runs in the heartbeat, which a throw would end for good.
    /// </remarks>
    private async Task RestoreLateSlotAsync(CancellationToken ct)
    {
        try
        {
            var root = _indexService.RootDir;
            if (string.IsNullOrEmpty(root) || PathComparer.SameDirectory(root, _lateRestoreTried)) return;
            // The start's own restore first: read before it lands, what it restores is taken for an untouched window.
            // VSTHRD003 is a false positive here, as in ShowAwayNoticeAsync; LoadSessionAsync never throws.
#pragma warning disable VSTHRD003
            await _startupSessionLoad;
#pragma warning restore VSTHRD003
            // Read off the VM context, as EverBound: checked again there before anything changes.
            var rootless = RootlessOnScreen;
            // A window that held a conversation of a workspace (restored, saved, or emptied by /clear on purpose) is never
            // filled behind the person's back: only the one built before its solution, still untouched.
            if (!rootless && _conversationRoot.EverBound) return;
            // Busy now (a command, a send starting): tried again at the next tick, not given up. A conversation restored
            // without a workspace is judged below, where a turn under way reads as something done.
            if (!rootless && await AskedInThisWindowAsync()) return;
            _lateRestoreTried = root;

            SessionData? loaded = null;
            try { loaded = await _store.LoadAutoSaveAsync(root, ct); }
            catch (UnreadableSessionException damaged)
            {
                // Damaged: the store kept a copy of it, named here — the next save writes over the original.
                Diagnostics.Swallow("Session.LateRestore", damaged);
                await RunOnVMContextAsync(() => InsertThemed(ChatMessageItem.NoticeMsg(damaged.Message)));
            }
            catch (Exception ex) when (rootless && ex is not OperationCanceledException)
            {
                // The conversation on screen is still not this solution's, whatever its slot holds.
                Diagnostics.Swallow("Session.LateRestore", ex);
            }
            if (loaded is not { } session || !session.Messages.Any(SessionManager.IsQuestion))
            {
                if (rootless) SayRootlessConversation(root, ownKept: false);
                return;
            }

            // ⚠ A turn begun while the slot was read is never settled here: SettleCurrentTurnAsync presses Stop. The check
            // below sees it (AskedOnScreen and UntouchedSinceRootlessRestore read IsLoading and _sendStarting).
            await RunOnVMContextAsync(() =>
            {
                // Checked again where the conversation is replaced: a question sent meanwhile keeps its conversation, and
                // the save that binds it archives the slot instead (ConversationArchive.KeepUnreadSlotAsync).
                if (rootless ? !UntouchedSinceRootlessRestore() : _conversationRoot.EverBound || AskedOnScreen())
                {
                    if (rootless) SayRootlessConversation(root, ownKept: true);
                    return;
                }
                // What the window said before the solution was known (a first-run notice, a window resized) is said once:
                // kept, after the conversation restored — never the conversation restored without a workspace, nor what
                // was said of it.
                var said = Messages.Where(m => !ReferenceEquals(m, _anchor0) && !ReferenceEquals(m, _anchor1)
                                               && _rootlessRestore?.Items.Contains(m) != true).ToList();
                RestoreConversation(session.Messages, session.State);
                foreach (var item in said) InsertThemed(item);
                _conversationRoot.Restored(root);
                _rootlessRestore = null;
                RefreshSessionsList();
                ScrollToBottom();
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Diagnostics.Swallow("Session.LateRestore", ex); }
    }

    /// <summary>The notice of the start was looked for (<see cref="ShowAwayNoticeAsync"/>): once per process — a solution
    /// opened later did not see Visual Studio close.</summary>
    private bool _awayAsked;

    /// <summary>
    /// The notice of a start (<see cref="Services.Presentation.AwayNotice"/>): what the closing of Visual Studio cut or
    /// left unread — the newest run it cut, the background tasks it stopped or nobody read —, above the restored
    /// conversation. Once per workspace, after the start's restore; dismissed, never said again.
    /// </summary>
    private async Task ShowAwayNoticeAsync()
    {
        try
        {
            var root = _indexService.RootDir;
            if (_awayAsked || string.IsNullOrEmpty(root)) return;
            // After the start's restore, late one included (a window built before its solution): the card goes above the
            // conversation, and the no-answer line under its question.
            if (!_conversationRoot.EverBound && !PathComparer.SameDirectory(root, _lateRestoreTried)) return;
            // VSTHRD003 is a false positive here, as in PendingPrompt: out of process, no JTF main thread to deadlock
            // against; LoadSessionAsync never throws.
#pragma warning disable VSTHRD003
            await _startupSessionLoad;
#pragma warning restore VSTHRD003
            _awayAsked = true;

            var queue = BackgroundTasks();
            var notice = Services.Presentation.AwayNotice.Build(_tools.History.Runs, ConversationStore.VisualStudioSlot,
                                                               "Visual Studio", queue.List(), queue.SaidRuns(), DateTime.Now, root,
                                                               Services.Rag.KeptIndexes.PruneHere(root));
            if (notice is null) return;
            await RunOnVMContextAsync(() =>
            {
                if (Messages.Any(m => m.Role == "away")) return;
                ChatMessageItem? card = null;
                card = ChatMessageItem.AwayMsg(notice,
                    review:  () => { if (notice.Run is { } run) _ = ReviewRunAsync(run.RunId, null, null, under: card); },
                    undo:    () => { if (notice.Run is { } run) _ = UndoFromResultBarAsync(run.RunId); },
                    dismiss: () =>
                    {
                        _ = Task.Run(() => queue.MarkSaid(notice.RunIds, notice.TaskIds));
                        _ = RunOnVMContextAsync(() => { if (card is not null) Messages.Remove(card); });
                    },
                    act: task => _ = HandleSlashCommandAsync(task.Retry ? $"/task retry {task.Id}" : $"/task {task.Id}",
                                                             CancellationToken.None),
                    kept: () => { if (notice.Pruned is { } pruned) _ = OpenSettingsPageAsync(pruned.Page, pruned.Section); });
                ApplyItemTheme(card);
                // Under a restored question no answer will follow: said there, and kept with the conversation.
                if (notice.Run is not null && !IsLoading && !_sendStarting
                    && Messages.LastOrDefault(m => m.Role is "user" or "assistant" && m.ToolName != SessionManager.NoticeMarker)
                           is { Role: "user", IsNote: false })
                    InsertThemed(ChatMessageItem.NoticeMsg(Strings.AwayNoAnswer("Visual Studio")));
                Messages.Insert(0, card);
            });
        }
        catch (Exception ex) { Diagnostics.Swallow("Session.AwayNotice", ex); }
    }

    /// <summary>Whether a question was asked in this window, or one is on its way — or the person set the conversation up
    /// (a <c>/template</c>, a mode held, Plan, a switch, a plan to follow): a restore replaces the whole state
    /// (<see cref="ApplyState"/>), and would take back what they chose.</summary>
    private bool AskedOnScreen() =>
        IsLoading || _sendStarting || CaptureState() with { CurrentName = null } != SessionState.New
        || Messages.Any(m => m.Saved is var s && SessionManager.IsQuestion(new SavedMessage(s.Role, s.Content, s.ToolName)));

    private async Task<bool> AskedInThisWindowAsync()
    {
        var asked = true;
        await RunOnVMContextAsync(() => asked = AskedOnScreen());
        return asked;
    }

    private async Task LoadSessionAsync(object? _, CancellationToken ct)
    {
        try
        {
            await SettleCurrentTurnAsync();

            string name = string.Empty;
            await RunOnVMContextAsync(() =>
                name = string.IsNullOrWhiteSpace(SelectedSession) ? "last_session" : SelectedSession);

            // The auto-save slot is the workspace's. The applied root may not be pinned yet at start-up, hence the
            // solution-anchored fallback.
            var here = string.IsNullOrEmpty(_indexService.RootDir) ? FindReliableProjectRoot() : _indexService.RootDir;

            // ⚠ A load that returns nothing is indistinguishable, on screen, from an ignored
            // click: the conversation does not move, the panel stays open, nothing is said.
            // LoadAsync returns null when the file is gone and THROWS on unreadable JSON — both
            // cases are therefore named (the exception, below, by the same message).
            SessionData? session;
            try
            {
                session = name == ConversationStore.AutoSaveSlot
                    ? await _store.LoadAutoSaveAsync(here, ct)
                    : await _store.LoadAsync(name, ct);
            }
            catch (Exception ex)
            {
                Diagnostics.Swallow($"Session.Load({name})", ex);
                // A damaged file names the copy kept of it — the auto-save writes over the original at the next turn.
                var said = ex is UnreadableSessionException damaged ? damaged.Message : Strings.SessionLoadFailed(name);
                await RunOnVMContextAsync(() => InsertThemed(ChatMessageItem.NoticeMsg(said)));
                return;
            }

            if (session is null || session.Messages.Count == 0
                || (name == "last_session" && !SessionManager.AutoSaveBelongsHere(session, here)))
            {
                await RunOnVMContextAsync(() =>
                {
                    if (Messages.Count == 0) { Messages.Add(_anchor0); Messages.Add(_anchor1); }
                    RefreshSessionsList();
                    // A session that is listed but empty or gone: say so. The only legitimately
                    // silent case is "no saved session", where there is nothing to load.
                    if (!string.IsNullOrEmpty(name) && name != "last_session")
                        InsertThemed(ChatMessageItem.NoticeMsg(Strings.SessionLoadFailed(name)));
                });
                return;
            }

            await RunOnVMContextAsync(() =>
            {
                // The auto-save slot carries the named session it continues: /branch keeps writing to that one.
                RestoreConversation(session.Messages,
                                    session.State with { CurrentName = name == "last_session" ? session.CurrentName : name });
                // Loaded here, it is this workspace's conversation from now on — whatever solution is opened next.
                _conversationRoot.Restored(here);
                // Restored without a workspace: what a solution opened later may replace (RestoreLateSlotAsync).
                _rootlessRestore = string.IsNullOrEmpty(here) && name == ConversationStore.AutoSaveSlot
                    ? new(CaptureState(), SavedTurns(), new(Messages, ReferenceEqualityComparer.Instance))
                    : null;
                IsSessionPanelOpen = false;
                RefreshSessionsList();
                ScrollToBottom();
            });
        }
        catch (Exception ex) { Diagnostics.Swallow("Session.Load", ex); }
    }

    /// <summary>
    /// Replaces the chat with a saved transcript: fresh system prompt, rebuilt API history and
    /// re-rendered bubbles. Shared by session loading and <c>/branch</c> (which restores the
    /// truncated transcript of the new branch). Must run on the VM context.
    /// </summary>
    /// <param name="state">Everything the conversation holds besides its messages, in place of the one left
    /// (<see cref="ApplyState"/>) — its name too (<c>null</c>: no session file, as the auto-save slot).</param>
    private void RestoreConversation(IReadOnlyList<SavedMessage> messages, SessionState state)
    {
        Messages.Clear();
        var modeGone              = ApplyState(state);
        _workspaceContextInjected = false;
        _sessionStartTime         = null;
        _threadReloaded           = messages.Count > 0;
        _lastRegenerableMsg       = null;
        _lastContinuableMsg       = null;
        _baseSystemPrompt         = BuildSystemPrompt();
        // The history the model had: compacted as it was, its recap in the system message — restored whole, the turns
        // that recap tells of are all there again.
        (_history, _summary)      = SessionManager.BuildRestoredHistory(_baseSystemPrompt, messages, _summary);
        if (_summary is null) _recapInSystem = string.Empty;
        ApplySystemPrompt();
        _conversationTurnCount    = messages.Count(SessionManager.IsQuestion);   // the recap's count goes on
        ResetTurnAccounting();
        // ⚠ Then measured — its OWN measure, never the one of the conversation left: zero is a first turn's state, and
        // the first question after a reload (Visual Studio reloads the last session every time it opens) went out
        // uncompacted however large the reload — every turn and tool output the screen kept.
        _lastPromptTokens = Services.Agent.AgentOrchestrator.EstimateTokens(_history);
        UpdateContextBudget();

        foreach (var m in messages)
        {
            var item = ChatMessageItem.FromSaved(m.Role, m.Content, m.ToolName ?? string.Empty, _config.ToolBubblesExpanded, m.Timestamp ?? string.Empty)
                                      .WithRun(m.Run);
            ApplyItemTheme(item);
            Messages.Add(item);
        }
        GroupRestoredSteps();

        Messages.Add(_anchor0);
        Messages.Add(_anchor1);
        // Said after the thread, which shows nothing of the mode it was held in.
        if (modeGone is not null) InsertThemed(ChatMessageItem.NoticeMsg(modeGone));
    }


    /// <summary>Toggles agent step mode (shared by the <c>/agent-step</c> command and the toolbar button).</summary>
    /// <remarks>Commands run OFF the VM context (see SendAsync) — [DataMember] mutations are
    /// marshalled, like every other property write.</remarks>
    private async Task ToggleStepModeAsync()
    {
        await RunOnVMContextAsync(() => IsStepMode = !IsStepMode);
        // Localized, like its TWIN pair of plan mode three lines below — in the VM as well as in
        // the host: an English literal here is read in English by nine users out of ten, with
        // nothing saying so.
        await ShowInfoAsync(IsStepMode ? Strings.StepModeOn : Strings.StepModeOff);
    }

    /// <summary>Toggles plan mode (shared by the <c>/plan</c> command and the toolbar button).
    /// Refreshes the system prompt so the plan-mode instructions follow the toggle mid-session.</summary>
    private async Task TogglePlanModeAsync()
    {
        await RunOnVMContextAsync(() =>
        {
            IsPlanMode = !IsPlanMode;   // marshalled like the history it drives
            if (IsPlanMode) LeaveHeldMode();   // one selector: Plan leaves the mode
            ChatMode   = CurrentMode();
            RefreshSystemPrompt();
        });
        // Localised through Strings, like every other command message: the toolbar button shows
        // these two lines too, in all ten languages.
        await ShowInfoAsync(IsPlanMode ? Strings.PlanModeOn : Strings.PlanModeOff);
    }

    /// <summary>
    /// Live-sync handler for <see cref="InferpalConfig.AgentModeEnabledChanged"/>: keeps the
    /// toolbar switch aligned when the Settings checkbox flips the value. Marshals to the VM context.
    /// Idempotent for the toolbar's own toggle (the property setters no-op when unchanged).
    /// </summary>
    private void OnAgentModeConfigChanged(bool enabled) => Post(() =>
    {
        IsAgentMode    = enabled;
        ChatMode       = CurrentMode();
    });

    /// <summary>The Settings window changed the UI language → re-localize every bound label live
    /// (welcome cards, input hints, tooltips) instead of waiting for the next window load.</summary>
    private void OnLanguageChanged() => Post(() => ApplyLabels());

    /// <summary>
    /// After a save, the window in use is measured again for the chat model: it was the one the LAST TURN measured, so
    /// after a new window or a new model X-Ray and the gauge kept the old number until the next question — a setting
    /// that looks as if it did not take. Fire-and-forget: the save is not held by a probe of the server.
    /// </summary>
    private void OnConfigSaved()
    {
        // ⚠ The model button names the configured chat model: chosen elsewhere — the settings window, /import apply — the
        // button kept the previous name while every question went to the new one. Every writer of the label sets it to
        // DefaultModel; a save is where a change made by someone else becomes visible.
        Post(() =>
        {
            if (!string.Equals(ActiveModelLabel, _config.DefaultModel, StringComparison.Ordinal))
                ActiveModelLabel = _config.DefaultModel;
            // A server moved to another machine, or back: where the code goes is said again.
            RefreshWelcomeLine();
        });
        _ = RemeasureContextWindowAsync();
    }

    private async Task RemeasureContextWindowAsync()
    {
        try
        {
            var window = await Services.Agent.ContextManager.EffectiveWindowAsync(
                _config, _client, ModelRouter.Resolve(_config, ModelRole.Chat), CancellationToken.None);
            // ⚠ Not RefreshSystemPrompt: a save can land mid-turn, and rewriting the system message changes the list the
            // agent loop is reading. The prompt is rebuilt at the start of every question anyway.
            await RunOnVMContextAsync(() =>
            {
                _contextWindowInUse = window;
                UpdateContextBudget();
            });
        }
        catch (Exception ex) { Diagnostics.Swallow("ContextWindow.RemeasureOnSave", ex); }
    }

    /// <summary>Releases a step-mode pause, letting the agent proceed to its next action.</summary>
    private void ResumeStep() => _stepResume?.TrySetResult(true);

    private async Task PauseForStepAsync(CancellationToken ct)
    {
        _stepResume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ChatMessageItem? pauseBubble = null;

        await RunOnVMContextAsync(() =>
        {
            pauseBubble = ChatMessageItem.NoticeMsg(Strings.AgentPausedForStep);
            pauseBubble.InitResumeCallback(() => Post(() => ResumeStep()));
            ApplyItemTheme(pauseBubble);
            Messages.Insert(Messages.Count - 2, pauseBubble);
            ScrollToBottom();
        });

        try
        {
            await _stepResume.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Ensure TCS is completed so any racing awaiter unblocks cleanly
            _stepResume.TrySetCanceled(ct);
            throw;
        }
        finally
        {
            _stepResume = null;
            // Remove the pause bubble — it's no longer relevant after resume or cancel
            if (pauseBubble is not null)
            {
                Post(() =>
                {
                    var idx = Messages.IndexOf(pauseBubble);
                    if (idx >= 0) Messages.RemoveAt(idx);
                });
            }
        }
    }

    #endregion
}
