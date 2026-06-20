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
    #region Connexion, heartbeat, sessions & modes

    private async Task RetryConnectionAsync(object? _, CancellationToken ct)
    {
        _client.ResetCircuit();
        await _heartbeatCts.CancelAsync();
        _heartbeatCts = new CancellationTokenSource();
        // Keep _isBackendReachable=false until the next heartbeat tick confirms success —
        // this prevents a race where the user clicks Send during the 2-second check delay.
        await RunOnVMContextAsync(() =>
        {
            ConnectionStatusText  = "● …";
            ConnectionStatusColor = "#A0A0A0";
            ShowRetryButton       = false;
        });
        _ = StartHeartbeatAsync();
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
            catch { }
        }, null);
    }

    private async Task StartHeartbeatAsync()
    {
        var ct = _heartbeatCts.Token;
        try
        {
            // Let the window finish its initial render before the first check.
            await Task.Delay(2_000, ct);

            var presenter = new ConnectionStatusPresenter();

            while (!ct.IsCancellationRequested)
            {
                // Re-point RAG indexing when the user switches to a different solution.
                CheckSolutionSwitch();

                var url = _config.BaseUrl;
                var ok  = await _client.CheckConnectionAsync(url, ct);

                _isBackendReachable = ok; // volatile write — read by SendCoreAsync pre-flight

                var status = presenter.Evaluate(ok);

                await RunOnVMContextAsync(() =>
                {
                    ConnectionStatusText  = status.StatusText;
                    ConnectionStatusColor = status.StatusColor;
                    SendButtonColor       = status.SendButtonColor;
                    ShowRetryButton       = status.ShowRetry;

                    var edgeMessage = status.Transition switch
                    {
                        ConnectionTransition.Restored => Strings.MsgHeartbeatRestored,
                        ConnectionTransition.Lost     => Strings.MsgConnectionGuardFailed(url),
                        _                             => null,
                    };
                    if (edgeMessage is not null)
                    {
                        var msg = ChatMessageItem.AssistantMsg(edgeMessage);
                        ApplyItemTheme(msg);
                        Messages.Insert(Messages.Count - 2, msg);
                        ScrollToBottom();
                    }
                });

                // Connected: poll every 20 s (was 60 s) — fast enough to catch a Ollama crash
                // within one poll cycle without hammering the daemon.
                // Disconnected: poll every 8 s (was 15 s) — show recovery promptly.
                await Task.Delay(ok ? 20_000 : 8_000, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private Task ToggleSessionPanelAsync(object? _, CancellationToken ct) =>
        RunOnVMContextAsync(() => IsSessionPanelOpen = !IsSessionPanelOpen);

    // Triggered by SizeChanged on each message Border — fires AFTER WPF layout so ScrollIntoView
    // sees correct item dimensions. Two separate Posts ensure VS Remote UI sends distinct batches.
    private Task OnScrollToBottomAsync(object? _, CancellationToken ct) =>
        RunOnVMContextAsync(ScrollToBottom);

    private async Task ClearAsync(object? _, CancellationToken ct)
    {
        // Capture snapshot and first user message on the VM context before clearing.
        bool hasMessages = false;
        string firstUserContent = string.Empty;
        List<SavedMessage> snapshot = [];

        await RunOnVMContextAsync(() =>
        {
            hasMessages = Messages.Count > 0;
            if (!hasMessages) return;

            var firstUser    = Messages.FirstOrDefault(m => m.Role == "user");
            firstUserContent = firstUser?.Content ?? string.Empty;
            snapshot         = SessionManager.BuildSnapshot(
                Messages.Select(m => (m.Role, m.Content, m.ToolName, m.Timestamp)));
        });

        // Fire save+title generation in background so the UI clears immediately.
        if (hasMessages)
            _ = SaveNamedSessionAsync(firstUserContent, snapshot);

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
            _sessionTokens         = 0;
            _lastPromptTokens      = 0;
            TokenInfo              = string.Empty;
            HasContextBudget       = false;
            ContextFillPercent     = 0;
            ContextBudgetColor     = "#606060";
            HasBuildFailedBanner   = false;   // dismiss banner on /clear
        });
    }

    private async Task LoadSessionAsync(object? _, CancellationToken ct)
    {
        try
        {
            string name = string.Empty;
            await RunOnVMContextAsync(() =>
                name = string.IsNullOrWhiteSpace(SelectedSession) ? "last_session" : SelectedSession);

            var session = await _store.LoadAsync(name, ct);
            if (session is null || session.Messages.Count == 0)
            {
                await RunOnVMContextAsync(() =>
                {
                    if (Messages.Count == 0) { Messages.Add(_anchor0); Messages.Add(_anchor1); }
                    RefreshSessionsList();
                });
                return;
            }

            await RunOnVMContextAsync(() =>
            {
                Messages.Clear();
                _activeTemplateSuffix     = string.Empty;
                _workspaceContextInjected = false;
                _sessionStartTime         = null;
                _lastRegenerableMsg       = null;
                _baseSystemPrompt         = BuildSystemPrompt();
                _history                  = SessionManager.BuildRestoredHistory(_baseSystemPrompt, session.Messages);
                _oodaSummary              = string.Empty;
                _conversationTurnCount    = 0;

                foreach (var m in session.Messages)
                {
                    var item = ChatMessageItem.FromSaved(m.Role, m.Content, m.ToolName ?? string.Empty, _config.ToolBubblesExpanded, m.Timestamp ?? string.Empty);
                    ApplyItemTheme(item);
                    Messages.Add(item);
                }

                Messages.Add(_anchor0);
                Messages.Add(_anchor1);
                IsSessionPanelOpen = false;
                RefreshSessionsList();
                ScrollToBottom();
            });
        }
        catch { }
    }


    /// <summary>Toggles agent step mode (shared by the <c>/agent-step</c> command and the toolbar button).</summary>
    private async Task ToggleStepModeAsync()
    {
        IsStepMode = !IsStepMode;
        await ShowInfoAsync(IsStepMode
            ? "🦶 **Step mode ON** — the agent will pause after each tool call. Use ▶ Resume (or `/resume`) to continue."
            : "Step mode **OFF**.");
    }

    /// <summary>Toggles plan mode (shared by the <c>/plan</c> command and the toolbar button).
    /// Refreshes the system prompt so the plan-mode instructions follow the toggle mid-session.</summary>
    private async Task TogglePlanModeAsync()
    {
        IsPlanMode = !IsPlanMode;
        await RunOnVMContextAsync(() =>
        {
            _baseSystemPrompt = BuildSystemPrompt();
            if (_history.Count > 0 && _history[0].Role == "system")
                _history[0] = new ChatMessageDto("system", _baseSystemPrompt);
        });
        await ShowInfoAsync(IsPlanMode
            ? "📋 **Plan mode ON** — read-only: the agent explores and proposes a plan, but cannot edit files or run commands. Toggle again (or `/plan`) to apply changes."
            : "Plan mode **OFF**.");
    }

    /// <summary>
    /// Main-window switch between Chat and Agent mode. Flips the persisted
    /// <see cref="InferpalConfig.AgentModeEnabled"/> (controls <c>useOrchestrator</c> in
    /// <see cref="SendCoreAsync"/>) and saves immediately so the next turn picks it up.
    /// </summary>
    private async Task ToggleAgentModeAsync()
    {
        IsAgentMode = !IsAgentMode;
        _config.AgentModeEnabled = IsAgentMode;
        _config.Save();
        AgentModeLabel = IsAgentMode ? Strings.LabelModeAgent : Strings.LabelModeChat;
        await ShowInfoAsync(IsAgentMode
            ? $"**{Strings.LabelModeAgent}** — Plan → Act → Observe"
            : $"**{Strings.LabelModeChat}**");
    }

    /// <summary>
    /// Live-sync handler for <see cref="InferpalConfig.AgentModeEnabledChanged"/>: keeps the
    /// toolbar switch aligned when the Settings checkbox flips the value. Marshals to the VM context.
    /// Idempotent for the toolbar's own toggle (the property setters no-op when unchanged).
    /// </summary>
    private void OnAgentModeConfigChanged(bool enabled) => Post(() =>
    {
        IsAgentMode    = enabled;
        AgentModeLabel = enabled ? Strings.LabelModeAgent : Strings.LabelModeChat;
    });

    /// <summary>The Settings window changed the UI language → re-localize every bound label live
    /// (welcome cards, input hints, tooltips) instead of waiting for the next window load.</summary>
    private void OnLanguageChanged() => Post(() => ApplyLabels());

    /// <summary>Releases a step-mode pause, letting the agent proceed to its next action.</summary>
    private void ResumeStep() => _stepResume?.TrySetResult(true);

    private async Task PauseForStepAsync(CancellationToken ct)
    {
        _stepResume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ChatMessageItem? pauseBubble = null;

        await RunOnVMContextAsync(() =>
        {
            pauseBubble = ChatMessageItem.AssistantMsg(
                "⏸ **Agent paused after tool call.** Click ▶ Resume (or type `/resume`) to continue, or Cancel to abort.");
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
