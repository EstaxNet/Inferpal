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
    /// does not have is replaced by the best installed one, and said (<see cref="ModelCatalog.FirstModelToAdopt"/>).
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
            var listed = await _client.ListModelsAsync(CancellationToken.None).ConfigureAwait(false);
            if (ModelCatalog.FirstModelToAdopt(_config, listed) is not { } adopted) return;

            var configured = _config.DefaultModel;
            _config.DefaultModel = adopted;
            _config.Save();
            await RunOnVMContextAsync(() => ActiveModelLabel = adopted).ConfigureAwait(false);
            await FirstRunPresentAsync(Strings.MsgModelAdopted(configured, adopted)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Swallow("Connection.AdoptModel", ex); }
    }

    /// <summary>A backend chosen since this window's client was built applies at restart
    /// (<see cref="InferenceProviderFactory.PendingSwitch"/>): said instead of a "cannot reach" that blames it.</summary>
    private string BackendSwitchPendingText(string switchTo) => Strings.MsgBackendSwitchPending(
        InferenceProviderFactory.DisplayName(switchTo),
        InferenceProviderFactory.DisplayName(InferenceProviderFactory.CodeOf(_client)));

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

                var url = _config.BaseUrl;
                // A backend switched in Settings applies at restart: probing the new address with the previous
                // backend's client only yields a false "cannot reach" (InferenceProviderFactory.PendingSwitch).
                var switchTo = InferenceProviderFactory.PendingSwitch(_client, _config.Provider);
                var ok  = switchTo is null && await _client.CheckConnectionAsync(url, ct);

                _isBackendReachable = ok; // volatile write — read by SendCoreAsync pre-flight

                var status = presenter.Evaluate(ok, _client.ConnectionRefusal);

                await RunOnVMContextAsync(() =>
                {
                    SendButtonColor       = status.SendButtonColor;
                    ShowRetryButton       = status.ShowRetry;
                    _connected            = ok;
                    _refusal              = ok ? null : _client.ConnectionRefusal;
                    RefreshModelButton();

                    var edgeMessage = switchTo is not null
                        ? (switchTo == _announcedSwitch ? null : BackendSwitchPendingText(switchTo))
                        : status.Transition switch
                    {
                        ConnectionTransition.Restored => Strings.MsgHeartbeatRestored(
                                                             InferenceProviderFactory.DisplayName(_config.Provider)),
                        ConnectionTransition.Lost     => Strings.MsgConnectionLost(
                                                             url, InferenceProviderFactory.DisplayName(_config.Provider),
                                                             _client.ConnectionRefusal),
                        _                             => null,
                    };
                    _announcedSwitch = switchTo;   // said once per switch; a switch back is said by "Restored"
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

        // Capture snapshot and first user message on the VM context before clearing.
        bool hasMessages = false;
        string firstUserContent = string.Empty;
        List<SavedMessage> snapshot = [];

        await RunOnVMContextAsync(() =>
        {
            // Decided on the snapshot, never on Messages: the two scroll anchors are always there,
            // so counting them archived an empty, titled session on every /clear and /template.
            snapshot    = SessionManager.BuildSnapshot(
                Messages.Select(m => (m.Role, m.Content, m.ToolName, m.Timestamp)));
            hasMessages = snapshot.Count > 0;
            if (!hasMessages) return;

            var firstUser    = Messages.FirstOrDefault(m => m.Role == "user");
            firstUserContent = firstUser?.Content ?? string.Empty;
        });

        // Fire save+title generation in background so the UI clears immediately.
        if (hasMessages)
            _ = SaveNamedSessionAsync(firstUserContent, snapshot);

        // The conversation just discarded leaves the auto-save slot, or the next start brings it back.
        try { await _store.ForgetAutoSaveAsync(_indexService.RootDir, ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Swallow("Session.ForgetAutoSave", ex); }

        await RunOnVMContextAsync(() =>
        {
            Messages.Clear();
            Messages.Add(_anchor0);
            Messages.Add(_anchor1);
            _activeTemplateSuffix     = string.Empty;
            _workspaceContextInjected = false;
            _sessionStartTime         = null;
            _lastRegenerableMsg       = null;
            _baseSystemPrompt         = BuildSystemPrompt();
            _history               = [new("system", _baseSystemPrompt)];
            _oodaSummary           = string.Empty;
            _conversationTurnCount = 0;
            _currentSessionName    = string.Empty;   // the archived conversation keeps its own file
            ResetTurnAccounting();
            HasBuildFailedBanner   = false;   // dismiss banner on /clear
        });
    }

    private async Task LoadSessionAsync(object? _, CancellationToken ct)
    {
        try
        {
            await SettleCurrentTurnAsync();

            string name = string.Empty;
            await RunOnVMContextAsync(() =>
                name = string.IsNullOrWhiteSpace(SelectedSession) ? "last_session" : SelectedSession);

            // ⚠ A load that returns nothing is indistinguishable, on screen, from an ignored
            // click: the conversation does not move, the panel stays open, nothing is said.
            // LoadAsync returns null when the file is gone and THROWS on unreadable JSON — both
            // cases are therefore named (the exception, below, by the same message).
            SessionData? session;
            try
            {
                session = await _store.LoadAsync(name, ct);
            }
            catch (Exception ex)
            {
                Diagnostics.Swallow($"Session.Load({name})", ex);
                await RunOnVMContextAsync(() => InsertThemed(
                    ChatMessageItem.NoticeMsg(Strings.SessionLoadFailed(name))));
                return;
            }

            // The auto-save slot is one file for every project and both editors: another workspace's
            // conversation is not this one's to restore. The applied root may not be pinned yet at
            // start-up, hence the solution-anchored fallback.
            var here = string.IsNullOrEmpty(_indexService.RootDir) ? FindReliableProjectRoot() : _indexService.RootDir;
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
                                    name == "last_session" && !string.IsNullOrEmpty(session.CurrentName) ? session.CurrentName : name);
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
    private void RestoreConversation(IReadOnlyList<SavedMessage> messages, string sessionName)
    {
        Messages.Clear();
        _activeTemplateSuffix     = string.Empty;
        _workspaceContextInjected = false;
        _sessionStartTime         = null;
        _lastRegenerableMsg       = null;
        _baseSystemPrompt         = BuildSystemPrompt();
        _history                  = SessionManager.BuildRestoredHistory(_baseSystemPrompt, messages);
        _oodaSummary              = string.Empty;
        _conversationTurnCount    = 0;
        _currentSessionName       = sessionName == "last_session" ? string.Empty : sessionName;
        ResetTurnAccounting();
        // ⚠ Then measured — its OWN measure, never the one of the conversation left: zero is a first turn's state, and
        // the first question after a reload (Visual Studio reloads the last session every time it opens) went out
        // uncompacted however large the reload — every turn and tool output the screen kept.
        _lastPromptTokens = Services.Agent.AgentOrchestrator.EstimateTokens(_history);
        UpdateContextBudget();

        foreach (var m in messages)
        {
            var item = ChatMessageItem.FromSaved(m.Role, m.Content, m.ToolName ?? string.Empty, _config.ToolBubblesExpanded, m.Timestamp ?? string.Empty);
            ApplyItemTheme(item);
            Messages.Add(item);
        }
        GroupRestoredSteps();

        Messages.Add(_anchor0);
        Messages.Add(_anchor1);
    }


    /// <summary>Toggles agent step mode (shared by the <c>/agent-step</c> command and the toolbar button).</summary>
    /// <remarks>Commands run OFF the VM context (see SendAsync) — [DataMember] mutations are
    /// marshalled, like every other property write.</remarks>
    private async Task ToggleStepModeAsync()
    {
        await RunOnVMContextAsync(() => IsStepMode = !IsStepMode);
        // Localized: §17 had localized the TWIN pair of plan mode, three lines below, and left
        // this one as an English literal — in the VM as well as in the host. Nine users out of ten
        // read English there, with nothing saying so.
        await ShowInfoAsync(IsStepMode ? Strings.StepModeOn : Strings.StepModeOff);
    }

    /// <summary>Toggles plan mode (shared by the <c>/plan</c> command and the toolbar button).
    /// Refreshes the system prompt so the plan-mode instructions follow the toggle mid-session.</summary>
    private async Task TogglePlanModeAsync()
    {
        await RunOnVMContextAsync(() =>
        {
            IsPlanMode = !IsPlanMode;   // marshalled like the history it drives
            ChatMode   = CurrentMode();
            RefreshSystemPrompt();
        });
        // Localised since §17: these two lines were hard-coded English while every other command
        // message went through Strings — the toolbar button shows them too, in all ten languages.
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
    private void OnConfigSaved() => _ = RemeasureContextWindowAsync();

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
