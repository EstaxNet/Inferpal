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
    #region Sending & the chat turn

    // ── Handlers ───────────────────────────────────────────────────────────────

    private async Task SendAsync(object? _, CancellationToken ct)
    {
        // The send button doubles as a stop button: while a request is running it cancels
        // the in-flight request instead of starting a new one (the XAML shows a red square).
        // _currentCts is only wired once the request is actually in flight, so an extra Enter
        // during the brief startup window (before there is anything to cancel) is a no-op here
        // and is instead absorbed by the atomic claim below.
        if (IsLoading)
        {
            // Marshalled for the same reason as CancelAsync: the finally disposes _currentCts on
            // the VM context, and cancelling from here raced it.
            await RunOnVMContextAsync(() => _currentCts?.Cancel());
            return;
        }

        // If the @-mention popup is open, Enter accepts the first suggestion instead of
        // sending. Otherwise a typed mention like "@code Foo" / "@file Bar" would be sent
        // verbatim as chat text — the popup item is only reachable by mouse click, so the
        // feature looked dead from the keyboard. Mirrors standard autocomplete behaviour.
        if (HasMentionSuggestions)
        {
            MentionSuggestion? first = null;
            await RunOnVMContextAsync(() => first = MentionSuggestions.FirstOrDefault());
            if (first is not null)
            {
                await first.OnSelect(ct);
                return;
            }
        }

        // ── Atomic send claim ──────────────────────────────────────────────────────
        // SendAsync runs off the VM thread, and IsLoading only flips true deep inside
        // SendCoreAsync (after workspace + RAG context is built, which can be slow on a busy
        // backend). So two Enter presses arriving in that window would both pass the IsLoading
        // check above and send the prompt twice. The check-and-claim is therefore done on the
        // single-threaded, FIFO VM context: the first caller wins and sets _sendStarting; any
        // duplicate sees it (or IsLoading) already set and bails out. The claim is released
        // either here (slash / empty / unreachable paths) or, for a normal send, the instant
        // SendCoreAsync flips IsLoading on — from which point IsLoading is the guard.
        string               userText    = string.Empty;
        List<AttachmentItem> attachments = [];
        var                  claimed     = false;

        await RunOnVMContextAsync(() =>
        {
            if (_sendStarting || IsLoading) return;
            userText = Prompt.Trim();
            if (userText.Length == 0) return;
            attachments   = [..Attachments];
            _sendStarting = true;
            claimed       = true;
        });
        if (!claimed) return;

        try
        {
            if (userText.StartsWith('/'))
            {
                await RunOnVMContextAsync(() => Prompt = string.Empty);
                // Slash commands reach the file tools too (/tdd, /test, /onboard…).
                PinWorkspaceRoot();
                await HandleSlashCommandAsync(userText, ct);
                return;
            }

            await SendCoreAsync(userText, oneTimeModel: null, attachments, ct, clearPrompt: true);
        }
        finally
        {
            // Safety net: release the claim for any path that returned before SendCoreAsync
            // handed the guard over to IsLoading (slash command, backend unreachable, error).
            // For a normal send this is already false by now, so it is a harmless no-op.
            await RunOnVMContextAsync(() => _sendStarting = false);
        }
    }

    /// <param name="clearPrompt">
    /// <c>true</c> for normal chat sends (clears the input box and attachments).
    /// <c>false</c> for code-action auto-sends (preserves the user's current draft).
    /// </param>
    private async Task SendCoreAsync(
        string               userText,
        string?              oneTimeModel,
        List<AttachmentItem> attachments,
        CancellationToken    ct,
        bool                 clearPrompt = true)
    {
        // Every turn can reach the file tools: never before the workspace root is pinned.
        PinWorkspaceRoot();

        // ── Pre-flight: heartbeat says Ollama is offline → fail immediately ────────
        // This avoids the 30-minute HTTP timeout; the prompt stays in the input box
        // so the user can retry once Ollama is back up.
        if (!_isBackendReachable)
        {
            await RunOnVMContextAsync(() =>
            {
                var url = _config.BaseUrl;
                var msg = ChatMessageItem.AssistantMsg(Strings.MsgConnectionGuardFailed(
                    url, InferenceProviderFactory.DisplayName(_config.Provider)));
                ApplyItemTheme(msg);
                Messages.Insert(Messages.Count - 2, msg);
                ScrollToBottom();
            });
            return;
        }

        string                   historyText  = string.Empty;
        CancellationTokenSource? localCts     = null;
        var                      sendStart    = DateTime.UtcNow;

        try
        {
            _sessionStartTime ??= DateTime.Now;

            // ── Instant feedback ─────────────────────────────────────────────────────
            // Echo the user's message, clear the input and flip into the loading state NOW —
            // before the (potentially slow) workspace + RAG context building below, which on a slow
            // backend leaves the UI looking frozen for that whole window and invites a second
            // Enter. From this point IsLoading is the single re-entrancy guard, so the startup
            // claim (_sendStarting) is handed off / released.
            await RunOnVMContextAsync(() =>
            {
                _config.Save();
                if (_promptHistory.Append(userText)) // also resets navigation state
                    SavePromptHistory();
                if (clearPrompt)
                {
                    Prompt         = string.Empty;
                    Attachments.Clear();
                    HasAttachments = false;
                }
                // The turn is taken HERE, before the context build below: Stop cancels _currentCts
                // only, and that build can take seconds on a busy backend.
                localCts      = BeginOwnedTurn(ct);
                _sendStarting = false;
                // The bubble names what goes WITH the question. The chips were cleared just above:
                // without this line nothing — on screen, in the export, or in the session file —
                // said that a file, a selection or a @diff went with this turn, and a reloaded
                // session handed the model "explain this" without what "this" refers to
                // (Services/Agent/ChatTurnPolicy.BuildBubbleText).
                var userItem  = ChatMessageItem.UserMsg(Services.Agent.ChatTurnPolicy.BuildBubbleText(
                    userText, attachments.Select(a => a.Label).ToList()));
                ApplyItemTheme(userItem);
                Messages.Insert(Messages.Count - 2, userItem);
                ScrollToBottom();
            });

            // Build context-enriched history message (not shown in chat bubble).
            // The observable attachment chips are projected to the neutral (label, content)
            // pairs the pure policy works with.
            historyText = Services.Agent.ChatTurnPolicy.BuildHistoryText(
                userText,
                attachments.Select(a => new Services.Agent.AttachmentContent(a.Label, a.Content)).ToList());

            // First-turn workspace context: silently prepend solution + open editors
            if (!_workspaceContextInjected && !userText.StartsWith('/'))
            {
                // ⚠ The flag is set AFTER success. BuildWorkspaceContextAsync is best-effort —
                // both of its tools carry a 5-second deadline and return an empty string when they
                // fail — and the first turn of a session is exactly the one where Visual Studio is
                // still loading its solution. Set before, a timeout consumes the flag and the model
                // NEVER AGAIN gets the session's workspace context, with nothing saying so. "No
                // solution open" is not that case: get_solution_info then returns text.
                var workspaceCtx = await BuildWorkspaceContextAsync(localCts!.Token);
                if (!string.IsNullOrEmpty(workspaceCtx))
                {
                    _workspaceContextInjected = true;
                    historyText = workspaceCtx + "\n\n" + historyText;
                }
            }

            // Auto-context: retrieve and inject the most relevant indexed chunks for this turn,
            // skipping anything already attached. Guarantees per-turn RAG context even when the
            // debounced auto-attach chips did not fire (e.g. the user typed fast and sent).
            var autoCtx = await BuildAutoContextAsync(userText, attachments, localCts!.Token);
            if (!string.IsNullOrEmpty(autoCtx))
                historyText = autoCtx + "\n\n" + historyText;

            await RunOnVMContextAsync(() =>
            {
                // Stopped during the build: the question stays on screen and never reaches the history.
                localCts!.Token.ThrowIfCancellationRequested();
                // ⚠ The system prompt is re-read for EVERY question, as the host does. It carries files
                // the user and the agent edit — pinned files ("injected before each request", says the
                // setting), .inferpal/context.md, memory.md, the rules — and rebuilt only on a few
                // gestures (a toggle, /note, a change of active file when rules are glob-scoped), an
                // edited file reached the model in its OLD version for the rest of the session.
                RefreshSystemPrompt();
                _history.Add(new ChatMessageDto("user", historyText));
                // Shadow search is no longer needed — agent is running
                _shadowSearchCts?.Cancel();
                _shadowSearchCts?.Dispose();
                _shadowSearchCts = null;
            });
        }
        catch (OperationCanceledException) when (localCts is { IsCancellationRequested: true })
        {
            await RunOnVMContextAsync(() =>
            {
                InsertThemed(ChatMessageItem.AssistantMsg(Strings.MsgCancelled));
                ScrollToBottom();
                EndOwnedTurn(localCts);
            });
            return;
        }
        catch (Exception ex)
        {
            // The turn may already be taken (instant-feedback block above): release it here — this
            // catch is outside the main finally that normally does.
            var msg = ex.Message;
            await RunOnVMContextAsync(() =>
            {
                _sendStarting = false;
                EndOwnedTurn(localCts);
                InsertThemed(ChatMessageItem.AssistantMsg(Strings.MsgError(msg)));
            });
            return;
        }

        // Declared before the try block so it is accessible in the catch/finally handlers.
        // Needed to clean up an orphaned streaming bubble when cancellation or an exception
        // interrupts the stream before the normal RunOnVMContextAsync finalisation runs.
        ChatMessageItem? streamingMsg  = null;
        // Live-status bubble: inserted on first onStep callback and updated in place while the
        // agent works.  Removed before the final response is inserted so it never persists.
        ChatMessageItem? statusBubble  = null;

        // Creates the live-status bubble on first use, then updates it in place.
        void UpsertStatusBubble(string text) => Post(() =>
        {
            CurrentStep = text;
            if (statusBubble is null)
            {
                statusBubble = ChatMessageItem.StatusMsg(text);
                ApplyItemTheme(statusBubble);
                Messages.Insert(Messages.Count - 2, statusBubble);
                ScrollToBottom();
            }
            else
            {
                statusBubble.Content = text;
            }
        });

        // Removes the live-status bubble so it never persists in the conversation or on disk.
        // Must run on the VM context (called from within RunOnVMContextAsync below).
        void RemoveStatusBubble()
        {
            if (statusBubble is null) return;
            var sidx = Messages.IndexOf(statusBubble);
            if (sidx >= 0) Messages.RemoveAt(sidx);
            statusBubble = null;
        }

        // Background RAG indexing yields to this turn automatically: RunAgentAsync and
        // AgentOrchestrator.RunAsync each hold a GpuScheduler chat lease for their whole duration
        // (covers every chat entrypoint), so the chat model is never starved by the embedding workload.
        try
        {
            // Agent mode (Plan→Act→Observe) is active only when the user switch is on AND tools are
            // engaged AND no one-time model overrides this turn. Computed here so model selection and
            // the execution path below share one source of truth.
            var useOrchestrator = _config.AgentModeEnabled
                && oneTimeModel is null
                && _toolsEnabled;

            // Model selection: a one-time model (code actions) always wins; otherwise, only the actual
            // agent loop routes to the dedicated AgentModel if set, so a fast cache-friendly model
            // handles multi-turn tool work while a heavier model stays on plain chat. Chat mode keeps
            // DefaultModel even when tools are enabled (the basic tool-calling loop is still "chat").
            var effectiveModel =
                !string.IsNullOrEmpty(oneTimeModel)
                    ? oneTimeModel
                    : ModelRouter.Resolve(_config, useOrchestrator ? ModelRole.Agent : ModelRole.Chat);

            // After the model is known: the context check measures against the window the server
            // really loaded THIS model with, when that is smaller than the configured one.
            await CompactOrTruncateAsync(effectiveModel, localCts!.Token);

            using var sink = new ThrottledTokenSink(chunk => Post(() =>
            {
                if (streamingMsg is null)
                {
                    // ⚠ Through the funnel, like every other bubble. This one was inserted without
                    // being themed, and FinalizeStreamingBubble only themes it at the END of the
                    // turn: under a light theme the text kept the field's default — the dark
                    // theme's colour — on the window's light background, which is far below the
                    // readable contrast ratio. The answer was unreadable while it was generated,
                    // then appeared at once at the end.
                    //
                    // The convention guard could not see it: it looked only for bubbles built
                    // INSIDE the call to Messages.Insert(...), and this one is built a line above.
                    // It is the most-watched bubble in the product.
                    streamingMsg = InsertThemed(ChatMessageItem.StreamingMsg(effectiveModel));
                    ScrollToBottom();
                }
                streamingMsg.Content += chunk;
            }));

            // ── Real-time token meter ────────────────────────────────────────────────
            // Left to the end of the run, the header's context-fill bar and "tokens used" readout
            // stay frozen at the previous turn's value, so a long generation looks stalled. Seed
            // the gauge from the prompt about to be sent, then grow a rough estimate
            // (~4 chars/token, matching EstimateTokens) as answer and reasoning tokens stream in.
            // These are provisional (prefixed "~"); the run's final block snaps both to the real
            // prompt_eval_count + eval_count once the provider reports them.
            var runBasePromptTokens = Services.Agent.AgentOrchestrator.EstimateTokens(_history);
            Post(() => { _lastPromptTokens = runBasePromptTokens; UpdateContextBudget(); });

            var liveGenChars    = 0;
            var lastPostedChars = 0;
            void CountLive(int addedChars)
            {
                liveGenChars += addedChars;
                if (liveGenChars - lastPostedChars < 240) return;   // throttle: ~60 tokens between VM updates
                lastPostedChars = liveGenChars;
                var lastTurn = runBasePromptTokens + liveGenChars / 4;
                Post(() =>
                {
                    _lastPromptTokens = lastTurn;
                    UpdateContextBudget();
                    TokenInfo = Strings.TokenUsage($"~{lastTurn:N0}", $"~{_sessionTokens + lastTurn:N0}");
                });
            }

            // ── Choose execution path: Orchestrated (Plan→Act→Observe) vs basic loop ──
            IToolRegistry effectiveTools = (oneTimeModel is not null || !_toolsEnabled)
                ? EmptyToolRegistry.Instance
                : _tools;

            // Decorators when tools are enabled: plan mode filters to read-only tools
            // first, then step mode wraps whatever remains.
            if (ReferenceEquals(effectiveTools, _tools))
            {
                // Start a change-tracking run so this turn's file writes can be reverted via /undo-run.
                _tools.History.BeginRun();
                if (_planMode)      effectiveTools = new Services.Execution.PlanModeToolRegistry(_tools);
                if (_agentStepMode) effectiveTools = new Services.Execution.StepModeToolRegistry(effectiveTools, PauseForStepAsync);
            }

            // Common result variables filled by whichever path runs.
            string               agentFinalResponse = string.Empty;
            // How the run ENDED, when it is not because the model was done. Both facts lived in
            // OrchestratorResult all along and were read by NOBODY: a run cut short at its
            // iteration limit returned a fluent answer, indistinguishable from a task carried to
            // its end.
            string               agentEndNotice     = string.Empty;
            List<ToolExecution>  agentExecutions    = [];
            int                  agentTokensUsed    = 0;

            // Set true once tool bubbles have been streamed live (below), so the final
            // render pass skips re-inserting them and avoids duplicates.
            bool liveToolBubbles = false;

            // Builds the permanent result bubble for a finished tool execution
            // (shared by the live stream below and the final render pass).
            ChatMessageItem BuildToolBubble(ToolExecution exec)
            {
                var preview  = Services.Agent.ChatTurnPolicy.BuildToolPreview(exec.Output);
                var toolItem = ChatMessageItem.ToolMsg(exec.Name, Strings.MsgToolOutput(exec.Input, preview), _config.ToolBubblesExpanded);
                if (exec.Diff is not null)
                    toolItem.InitDiff(exec.Diff);
                if (exec.HasErrors)
                    toolItem.InitFixCallback(exec.Output, rawErrors => Post(() => Prompt = BuildFixPrompt(rawErrors)));
                ApplyItemTheme(toolItem);
                return toolItem;
            }

            // Inserts a tool-result bubble live, above the live status / streaming bubbles
            // so completed tools stack in chronological order while the agent keeps working.
            void StreamToolBubble(ToolExecution exec) => Post(() =>
            {
                liveToolBubbles = true;
                var toolItem = BuildToolBubble(exec);
                int idx = Messages.Count - 2;
                if (statusBubble is not null) { var i = Messages.IndexOf(statusBubble); if (i >= 0) idx = Math.Min(idx, i); }
                if (streamingMsg  is not null) { var i = Messages.IndexOf(streamingMsg);  if (i >= 0) idx = Math.Min(idx, i); }
                if (idx < 0) idx = 0;
                Messages.Insert(idx, toolItem);
                ScrollToBottom();
            });

            // Live reasoning preview (see ThinkingPreview): the most recent slice of a thinking
            // model's chain-of-thought is pushed into the status bubble so a long reasoning phase
            // reads as "the model is working" instead of a blank bubble. This deliberately never
            // touches the streaming/answer bubble, so the empty-bubble guards (see bug history)
            // are unaffected.
            var thinkingPreview = new Services.Presentation.ThinkingPreview();
            void OnThinking(string delta)
            {
                // Reasoning tokens are generated too — count them so the gauge moves during a long
                // thinking phase (reasoning models can spend most of a turn here before any answer).
                CountLive(delta.Length);
                // Plain chat mode: a reasoning model (qwen3.6…) streams its whole chain-of-thought
                // into the reasoning channel, and surfacing that raw text in the status reads as
                // stray "agent" output — it can even be off-topic deliberation, not the answer. Show
                // a generic "thinking" indicator instead (still proves the model is working), but keep
                // the throttle so the RPC rate stays bounded. The orchestrated agent path is left
                // untouched: there the live reasoning tail is genuine step progress.
                if (!useOrchestrator)
                {
                    if (thinkingPreview.Append(delta) is not null)
                        UpsertStatusBubble("\U0001F4AD " + Strings.StatusThinking);
                    return;
                }
                if (thinkingPreview.Append(delta) is { } text)
                    UpsertStatusBubble(text);
            }

            if (useOrchestrator)
            {
                // Orchestrated path: generates an explicit plan, then runs the
                // Plan → Act → Observe loop with live step-status tracking.
                ChatMessageItem?          planBubble  = null;
                Models.AgentPlan? activePlan = null;

                var orchResult = await _orchestrator.RunAsync(
                    model:         effectiveModel,
                    history:       _history,
                    tools:         effectiveTools,
                    onStep:        step  => UpsertStatusBubble("⏳ " + step),
                    onToken:       token => { sink.Append(token); CountLive(token.Length); },
                    onPlanReady:   plan  => Post(() =>
                    {
                        activePlan = plan;
                        planBubble = ChatMessageItem.AgentPlanMsg(plan);
                        ApplyItemTheme(planBubble);
                        // Insert plan bubble just before the scroll anchors.
                        Messages.Insert(Messages.Count - 2, planBubble);
                        ScrollToBottom();
                    }),
                    onStepUpdate:  (_, _) => Post(() =>
                    {
                        // Re-render the plan markdown with updated step emojis.
                        if (planBubble is not null && activePlan is not null)
                            planBubble.RefreshPlan(activePlan);
                    }),
                    onToolExecuted: StreamToolBubble,
                    onStreamReset: () =>
                    {
                        // Drop buffered tokens synchronously FIRST so a pending timer flush cannot
                        // resurrect the bubble after it is removed below (would duplicate the response).
                        sink.Reset();
                        thinkingPreview.Reset(); // stale reasoning from the finished act must not bleed into the next
                        Post(() =>
                        {
                        // Discard the intermediate streaming bubble entirely.
                        // Setting Content = "" was insufficient: some model outputs
                        // (lone "---", partial timer-flush tokens, whitespace runs)
                        // passed HasPrintableText/HasBlocks guards and left a visually
                        // empty bubble visible.  By removing the item from Messages and
                        // nulling streamingMsg, we guarantee the final response bubble
                        // is created fresh from the very first token of the last act —
                        // so it can never contain intermediate tool-calling rationale.
                        if (streamingMsg is not null)
                        {
                            var idx = Messages.IndexOf(streamingMsg);
                            if (idx >= 0) Messages.RemoveAt(idx);
                            streamingMsg = null;
                        }
                        });
                    },
                    ct: localCts.Token,
                    onThinking: OnThinking);

                agentFinalResponse = orchResult.FinalResponse;
                agentExecutions    = orchResult.Executions;
                agentTokensUsed    = orchResult.TokensUsed;
                agentEndNotice     = ChatTurnPolicy.EndNotice(
                    orchResult.ReachedIterationLimit, orchResult.WasLoopDetected, orchResult.AnswerCut);
            }
            else
            {
                // Basic path: unchanged 20-turn reactive loop.
                // Code actions use EmptyToolRegistry → Quick timeout; tool-enabled chat → Normal.
                var loopComplexity = effectiveTools == EmptyToolRegistry.Instance
                    ? TaskComplexity.Quick
                    : TaskComplexity.Normal;
                var result = await _client.RunAgentAsync(
                    model:      effectiveModel,
                    history:    _history,
                    tools:      effectiveTools,
                    onStep:     step  => UpsertStatusBubble("⏳ " + step),
                    onToken:    token => { sink.Append(token); CountLive(token.Length); },
                    ct:         localCts.Token,
                    complexity: loopComplexity,
                    onToolExecuted: StreamToolBubble,
                    onThinking: OnThinking);

                agentFinalResponse = result.FinalResponse;
                agentEndNotice     = ChatTurnPolicy.EndNotice(false, result.WasLoopDetected, result.AnswerCut);
                agentExecutions    = result.Executions;
                agentTokensUsed    = result.TokensUsed;
            }

            sink.Stop();

            // ── Render results — same for both paths ─────────────────────────────
            // RunOnVMContextAsync posts AFTER all onToken posts — FIFO guarantees
            // streamingMsg is fully populated when this action executes.
            await RunOnVMContextAsync(() =>
            {
                // Remove the live-status bubble before inserting the real results so it
                // never ends up persisted in the conversation or saved to disk.
                RemoveStatusBubble();

                var insertIdx = streamingMsg is not null
                    ? Messages.IndexOf(streamingMsg)
                    : Messages.Count - 2;

                // Tool bubbles are normally streamed live (StreamToolBubble); only insert them
                // here when no live streaming happened (e.g. a path that didn't wire the callback).
                if (!liveToolBubbles)
                    foreach (var exec in agentExecutions)
                        Messages.Insert(insertIdx++, BuildToolBubble(exec));

                // Multi-file recap: if ≥2 files were written/patched, add a summary bubble with "Restore All"
                var modifiedPaths = Services.Agent.ChatTurnPolicy.ModifiedFilePaths(agentExecutions);
                if (modifiedPaths.Count >= 2)
                {
                    var fileNames    = modifiedPaths.Select(Path.GetFileName).ToList();
                    var recapContent = Strings.MultiFileRecapTitle(modifiedPaths.Count, string.Join(", ", fileNames));
                    var recapItem    = ChatMessageItem.AssistantMsg(recapContent);
                    var capturedPaths = modifiedPaths;
                    recapItem.InitRestoreCallback(() => Post(() => _ = RestoreAllFilesAsync(capturedPaths)));
                    ApplyItemTheme(recapItem);
                    Messages.Insert(insertIdx, recapItem);
                }

                ChatMessageItem? lastAssistant = null;

                // A visually empty bubble is discarded so the fallback chain below can
                // show something meaningful instead.
                streamingMsg = FinalizeStreamingBubble(streamingMsg);
                if (streamingMsg is not null)
                    lastAssistant = streamingMsg;

                // Fallback chain when the streamed bubble was absent or visibly empty:
                // stored final response → tool summary → absolute "empty response" fallback.
                switch (Services.Agent.ChatTurnPolicy.DecideFinalAnswer(
                            streamingBubbleVisible: streamingMsg is not null,
                            finalResponse:          agentFinalResponse,
                            executionCount:         agentExecutions.Count))
                {
                    case Services.Agent.FinalAnswerKind.FinalText:
                        var finalMsg = ChatMessageItem.AssistantMsg(
                            Services.Presentation.MarkdownParser.StripThinkTags(agentFinalResponse));
                        ApplyItemTheme(finalMsg);
                        Messages.Insert(Messages.Count - 2, finalMsg);
                        lastAssistant = finalMsg;
                        break;

                    case Services.Agent.FinalAnswerKind.ToolSummary:
                        var summaryMsg = ChatMessageItem.AssistantMsg(
                            Strings.MsgAgentDone(Services.Agent.ChatTurnPolicy.BuildToolSummary(agentExecutions)));
                        ApplyItemTheme(summaryMsg);
                        Messages.Insert(Messages.Count - 2, summaryMsg);
                        lastAssistant = summaryMsg;
                        break;

                    case Services.Agent.FinalAnswerKind.EmptyFallback:
                        // ⚠ The message NAMES what was observed — requested model, server — instead
                        // of betting on a cause. The old text asserted "the model may not support
                        // text generation, try the chat model": reported from a machine on the
                        // network, and measuring the server it accused showed it perfectly healthy
                        // for that model — listing, chat, streaming, 28 tools, forced tool_choice.
                        // The advice therefore sent the user hunting exactly where the fault was
                        // not.
                        var emptyMsg = ChatMessageItem.AssistantMsg(
                            Strings.MsgEmptyResponseFrom(effectiveModel, _config.BaseUrl));
                        ApplyItemTheme(emptyMsg);
                        Messages.Insert(Messages.Count - 2, emptyMsg);
                        lastAssistant = emptyMsg;
                        break;

                    // FinalAnswerKind.StreamedAnswer: lastAssistant is already the streamed bubble.
                }

                // The answer STAYS - that is the original arbitration, "do not alarm when real work
                // was done". What changes is that it stops passing for a task carried to its end: a
                // discreet line follows it, saying why the run stopped. Without it, the only
                // difference between "I finished" and "I was cut short" lived in a boolean nothing
                // read.
                if (agentEndNotice.Length > 0)
                    InsertThemed(ChatMessageItem.AssistantMsg(agentEndNotice));

                if (lastAssistant is not null)
                    MarkRegeneratable(lastAssistant);

                ScrollToBottom();
                // Persist a CLEAN durable history: the pre-run history (which already ends with
                // the user's message) plus the one assistant answer shown in the chat. The run's
                // internal transcript (plan prompts, plan JSON, OBSERVE injections, capped tool
                // results, the tool list appended to the system prompt) must NOT leak across
                // turns: it bloats every following prompt and, worse, teaches the model by
                // example to replay its previous final answer verbatim instead of answering the
                // new question (observed with devstral: turn 2 returned turn 1's answer
                // word-for-word). This also matches what a session save/reload would rebuild.
                var persistedAnswer = Services.Agent.ChatTurnPolicy.ChoosePersistedAnswer(
                    lastAssistant?.Content, agentFinalResponse);
                if (persistedAnswer.Length > 0)
                    _history.Add(new ChatMessageDto("assistant", persistedAnswer));
                // The real prompt size of the run's last call reflects the discarded internal
                // transcript, not what the next turn will send — estimate from the durable
                // history instead so CompactOrTruncateAsync and the budget badge stay honest.
                _lastPromptTokens = Services.Agent.AgentOrchestrator.EstimateTokens(_history);
                _sessionTokens   += agentTokensUsed;
                _conversationTurnCount++;
                if (agentTokensUsed > 0)
                    TokenInfo = Strings.TokenUsage($"{agentTokensUsed:N0}", $"{_sessionTokens:N0}");
                UpdateContextBudget();
            });

            if (_config.OodaTurnThreshold > 0 && _conversationTurnCount % _config.OodaTurnThreshold == 0)
                await RunOodaSummaryAsync(localCts.Token);

            _ = AutoSaveAsync();
        }
        catch (OperationCanceledException)
        {
            await RunOnVMContextAsync(() =>
            {
                // Remove the live-status bubble (if any) before showing the "Cancelled" message.
                RemoveStatusBubble();
                // Finalize and discard any empty/invisible streaming bubble that was started
                // before the cancellation arrived (a visible partial response stays).
                streamingMsg = FinalizeStreamingBubble(streamingMsg);
                var cancelItem = ChatMessageItem.AssistantMsg(Strings.MsgCancelled);
                ApplyItemTheme(cancelItem);
                Messages.Insert(Messages.Count - 2, cancelItem);
                ScrollToBottom();
            });
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            await RunOnVMContextAsync(() =>
            {
                // Remove the live-status bubble (if any) before showing the error message.
                RemoveStatusBubble();
                streamingMsg = FinalizeStreamingBubble(streamingMsg);
                var errItem = ChatMessageItem.AssistantMsg(Strings.MsgError(msg));
                ApplyItemTheme(errItem);
                Messages.Insert(Messages.Count - 2, errItem);
                ScrollToBottom();
            });
        }
        finally
        {
            // Background RAG indexing resumes automatically when RunAgentAsync releases its
            // GpuScheduler chat lease — no manual gate balancing needed here anymore.
            await RunOnVMContextAsync(() =>
            {
                // Safety-net: remove any leftover status bubble in case the main path
                // or the catch handlers didn't reach the removal code.
                RemoveStatusBubble();
                // The tracking run opened above closes HERE: without this, everything written
                // after the turn (a /restore, a tool launched by a slash command) still attached to
                // it, and /undo-run reverted that along with the run we had just watched.
                _tools.History.EndRun();
                // Only the turn that still owns the state releases it (see EndOwnedTurn).
                EndOwnedTurn(localCts);
            });

            // Beep when a long agent run completes, so the user can work elsewhere
            if ((DateTime.UtcNow - sendStart).TotalSeconds > 30)
                NotifyAgentComplete();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool MessageBeep(uint uType);

    private static void NotifyAgentComplete()
    {
        try { MessageBeep(0x00000040); } // MB_ICONINFORMATION — asterisk/ding
        catch (Exception ex) { Diagnostics.Swallow("ChatTurn.Cleanup", ex); }
    }

    private Task CancelAsync(object? _, CancellationToken ct) =>
        // Marshalled: the finally disposes _currentCts on the VM context, so cancelling from the
        // command thread raced it into an ObjectDisposedException when the click landed exactly
        // as the response completed. Same context ⇒ serialized.
        RunOnVMContextAsync(() => _currentCts?.Cancel());

    // ── Connection Guard / Heartbeat ───────────────────────────────────────────

    #endregion
}
