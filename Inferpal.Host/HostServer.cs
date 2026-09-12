using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Docs;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Mcp.OAuth;
using Inferpal.Services.Persistence;
using Inferpal.Services.Rag;
using StreamJsonRpc;

namespace Inferpal.Host;

/// <summary>
/// JSON-RPC target exposing the Core to an editor adapter over stdio. Editor→host requests:
/// `initialize`, `chat/send` (streamed via `chat/*` notifications), `chat/cancel`, `chat/reset`,
/// `command/slash`, `codeAction/run`, `models/list`, `connection/check`, `config/get|update`, `fim/complete`,
/// `index/start|status`, `shutdown`; editor→host notifications: `textDocument/didOpen|didChange|didClose` (dirty-buffer
/// overlay) and `editor/didChangeActiveDocument`. Host→editor requests are issued by
/// <see cref="RpcEditorSurface"/> and <see cref="RpcApprovalService"/>.
/// </summary>
internal sealed partial class HostServer : IDisposable
{
    /// <summary>
    /// The editor on the other end of this RPC, as the model is told it. The host serves exactly
    /// one adapter today; the day it serves a second, this becomes a handshake field rather than a
    /// constant — it is never inferred from the process tree.
    /// </summary>
    internal const string EditorName = "Visual Studio Code";

    private readonly Func<InferpalConfig, IInferenceProvider> _providerFactory;
    private readonly Func<InferpalConfig> _configFactory;
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();

    private JsonRpc?                 _rpc;

    /// <summary>
    /// Locale the editor announced at <c>initialize</c>, kept so it can be re-applied.
    /// </summary>
    /// <remarks>
    /// It has to be kept, and re-applied after the configuration is loaded: <c>Config.Load</c>
    /// calls <c>Strings.ApplyLanguage(cfg.Language)</c> unconditionally, so an empty language
    /// ("Auto", the default) resets the override to null and the strings fall back to the machine's
    /// UI culture — silently discarding what the editor just said. Found by running the host, not by
    /// reading it: with `locale: "en"` the answers came back in French on a French Windows.
    /// </remarks>
    private string?                  _editorLocale;
    private HostSession?             _session;
    private CancellationTokenSource? _chatCts;

    /// <param name="providerFactory">Test seam; defaults to <see cref="InferenceProviderFactory.Create"/>.</param>
    /// <param name="configFactory">Test seam; defaults to <see cref="InferpalConfig.Load"/> so tests
    /// never read (or risk writing) the developer's real per-machine config.</param>
    public HostServer(Func<InferpalConfig, IInferenceProvider>? providerFactory = null,
                      Func<InferpalConfig>? configFactory = null)
    {
        _providerFactory = providerFactory ?? InferenceProviderFactory.Create;
        _configFactory   = configFactory   ?? InferpalConfig.Load;

        // §22. This host talks to its editor over RPC, never to a Visual Studio in-process package —
        // yet the signal folder under %TEMP% is machine-wide, so without this declaration
        // `get_solution_info` answered with the solution open in Visual Studio and
        // `get_debugger_state` with Visual Studio's break state. Declared, not inferred: a process
        // that guesses its own role guesses wrong the day a third front-end appears.
        SignalScope.DeclareNoVsInProcessPeer();
    }

    /// <summary>Completed when the adapter sent `shutdown` — Program.cs exits on it.</summary>
    public Task ShutdownRequested => _shutdown.Task;

    /// <summary>Session under test — lets the headless protocol tests observe state
    /// (overlay, history) that the wire protocol deliberately doesn't expose.</summary>
    internal HostSession? CurrentSession => _session;

    /// <summary>Wires the connection used for reverse requests and notifications.
    /// Must be called before the connection starts listening.</summary>
    public void Attach(JsonRpc rpc) => _rpc = rpc;

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
    public InitializeResult Initialize(InitializeParams p)
    {
        var rpc = _rpc ?? throw new InvalidOperationException("RPC connection not attached.");
        _editorLocale = NormalizeLocale(p.Locale);

        var config   = _configFactory();
        // AFTER the load, never before: Config.Load applies its own language and clears the
        // override when the user picked "Auto". An explicit choice in the settings still wins — it
        // is the more deliberate signal — and the editor's locale beats the machine's culture.
        ApplyLanguage(config);
        var client   = _providerFactory(config);
        var overlay  = new OpenDocumentOverlay();
        var editor   = new RpcEditorSurface(rpc, overlay);
        var approval = new RpcApprovalService(config, () => p.RootDir, rpc);
        var lsp      = new LspSemanticProvider();
        var index    = new ProjectIndexService(client, config, lsp);
        // Off Windows the default DPAPI protection is unavailable, so the token store fails loud
        // by design and MCP OAuth is unusable; route it through the editor's own secret store
        // (VS Code SecretStorage → OS keychain) instead. Windows keeps DPAPI: no round-trip, and
        // the file stays shared with the Visual Studio extension.
        var secrets  = OperatingSystem.IsWindows() ? null : new RpcSecretStore(rpc);
        var tokens   = secrets is null
            ? new McpTokenStore(McpTokenStore.DefaultPath)
            : new McpTokenStore(McpTokenStore.DefaultPath, secrets.Protect, secrets.Unprotect);
        var mcp      = new McpToolService(config, approval, clientFactory: null, tokenStore: tokens);
        var docs     = new DocsIndexService(client, config);
        // Registered only when the adapter declared `debug/*` support: an unimplemented handler
        // would answer "method not found" to every call, and the model would spend tokens each turn
        // on two tools that can only fail.
        var debug    = p.Debug ? new RpcDebugSession(rpc, declared: true) : null;
        var tools    = new ToolRegistry(editor, approval, config, index, client,
                                        new ProjectMapService(editor, index), mcp, docs, overlay, debug);

        // Pin the file-tool confinement root even when RAG never indexes; indexing itself starts
        // once the session exists (below).
        index.SetRoot(p.RootDir);

        // Slot-held: a re-entrant initialize used to dispose MCP/shells/tools under a running
        // agent loop (pre-1.6.0 architecture review, §2.6 — the adapter never does it today, but the invariant
        // belongs here, not in the adapter's good manners).
        WithTurnSlot("initialize", () => _session?.Dispose());
        _session = new HostSession
        {
            Config       = config,
            Client       = client,
            Overlay      = overlay,
            Editor       = editor,
            Tools        = tools,
            Orchestrator = new AgentOrchestrator(client, config),
            Index        = index,
            Docs         = docs,
            Mcp          = mcp,
            Lsp          = lsp,
            RootDir      = p.RootDir,
            Approval     = approval,
            TestCapture  = p.Debug ? new RpcTestDebugCapture(rpc) : null,
            Debug        = debug,
        };
        ResetHistory(_session);

        // As in Visual Studio: with RAG on, the workspace is indexed without being asked. No adapter
        // calls `index/start`, so without this `search_codebase` and the per-turn auto-context stayed
        // empty for the whole session.
        if (config.RagEnabled && !string.IsNullOrEmpty(p.RootDir))
            index.StartIndexing(p.RootDir);

        var caps = client.Capabilities;
        return new InitializeResult(
            HostVersion:     typeof(HostServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            Provider:        $"{config.Provider}",
            DefaultModel:    config.DefaultModel,
            ModelManagement: caps.ModelManagement,
            VramMonitoring:  caps.VramMonitoring,
            Fim:             caps.Fim,
            KeepAlive:       caps.KeepAlive);
    }

    [JsonRpcMethod("shutdown")]
    public void Shutdown()
    {
        lock (_gate) _chatCts?.Cancel();
        _shutdown.TrySetResult();
    }

    // ── Chat ───────────────────────────────────────────────────────────────────

    [JsonRpcMethod("chat/send", UseSingleObjectParameterDeserialization = true)]
    public async Task<ChatSendResult> ChatSendAsync(ChatSendParams p, CancellationToken ct)
    {
        var s   = Session();
        var cts = AcquireTurn(ct);

        // Mirror of the streamed text: returned as the partial answer on cancellation.
        var streamed = new StringBuilder();
        void OnToken(string t) { streamed.Append(t); Notify("chat/token", new { text = t }); }
        // ⚠ The reasoning channel was relayed RAW, one notification per delta - thousands of
        // JSON-RPC messages over stdio for one thinking phase of a reasoning model - and the
        // adapter threw the text away. Both halves were wrong: nothing bounded the rate, and what
        // it carried was read by nobody. ThinkingPreview (the same one the Visual Studio window
        // uses) accumulates, bounds the memory, and emits at most every 120 ms.
        var thinking = new ThinkingPreview();

        // And the TEXT only goes out on the agent path. In plain chat a reasoning model pours out
        // its whole deliberation - sometimes off-topic - and showing it reads as stray output: we
        // then send a heartbeat only, and the adapter shows its generic indicator. Same arbitration
        // as the VM, at the same place in the code.
        var showReasoningTail = false;
        void OnThinking(string t)
        {
            if (thinking.Append(t) is not { } tail) return;
            Notify("chat/thinking", new { text = showReasoningTail ? tail : null });
        }

        try
        {
            // Auto-context: inject the most relevant indexed chunks for this turn (same
            // per-turn RAG block as the VS VM). Runs before the agent takes the GPU lease.
            var promptText = p.Prompt;
            var autoCtx    = await BuildRagAutoContextAsync(s, promptText, p.AttachedPaths, cts.Token);
            if (!string.IsNullOrEmpty(autoCtx))
                promptText = autoCtx + "\n\n" + promptText;

            s.History.Add(new ChatMessageDto("user", promptText));

            // Pre-send context check.
            // It did not exist here. The history was never bounded: it grew until it went past the
            // model's num_ctx, and it was the backend that dropped its head - system prompt
            // included - in silence. And the settings panel offered compactionEnabled,
            // contextWindowKeepTurns and compactionTimeoutSeconds, three controls with no effect.
            // Same service as the Visual Studio window, same order: after the user message.
            var ctxDecision = await Services.Agent.ContextManager.PrepareAsync(
                s.History, s.Config, s.Client, s.LastPromptTokens,
                onStep: step => Notify("chat/step", new { text = step }),
                ct: cts.Token);

            if (ctxDecision.Outcome != Services.Agent.ContextOutcome.None)
            {
                if (ctxDecision.Outcome == Services.Agent.ContextOutcome.Compacted)
                    Services.Agent.HistoryCompaction.ApplySummary(s.History, ctxDecision.Plan, ctxDecision.Summary!);
                else
                    Services.Agent.HistoryCompaction.ApplyTruncation(s.History, ctxDecision.Plan);

                s.LastPromptTokens = 0;
                Notify("chat/tool", new ToolNotice("context_compact", string.Empty, ctxDecision.Notice, false));
            }
            // The session-scoped `/tools off` switch forces plain chat, like the VS VM.
            var agentMode = (p.AgentMode ?? s.Config.AgentModeEnabled) && s.ToolsEnabled;
            // The reasoning tail is step progress only on the orchestrated path.
            showReasoningTail = agentMode;
            // An explicit per-request model always wins; otherwise the Model Router applies the
            // same role chains as the VS adapter (agent loop → AgentModel, plain chat → DefaultModel).
            var model     = !string.IsNullOrWhiteSpace(p.Model)
                ? p.Model!
                : ModelRouter.Resolve(s.Config, agentMode ? ModelRole.Agent : ModelRole.Chat);

            if (agentMode)
            {
                // Plan mode restricts to read-only tools; step mode pauses after each call.
                IToolRegistry effectiveTools = s.Tools;
                if (s.PlanMode) effectiveTools = new PlanModeToolRegistry(effectiveTools);
                if (s.StepMode) effectiveTools = new StepModeToolRegistry(effectiveTools, tok => PauseForStepAsync(s, tok));

                // Group this run's file snapshots so the adapter can offer /undo-run semantics.
                s.Tools.History.BeginRun();
                var result = await s.Orchestrator.RunAsync(
                    model, s.History, effectiveTools,
                    onStep:         step => Notify("chat/step", new { text = step }),
                    onToken:        OnToken,
                    onPlanReady:    plan => Notify("chat/plan", new
                                    {
                                        goal  = plan.Goal,
                                        steps = plan.Steps.Select(st => st.Description).ToArray(),
                                    }),
                    onStepUpdate:   (i, status) => Notify("chat/stepUpdate", new { index = i, status = $"{status}" }),
                    onToolExecuted: te => Notify("chat/tool", new ToolNotice(te.Name, te.Input, te.Output, te.HasErrors)),
                    onStreamReset:  () => { streamed.Clear(); Notify("chat/streamReset", new { }); },
                    ct:             cts.Token,
                    onThinking:     OnThinking);

                s.History          = result.UpdatedHistory;
                s.LastPromptTokens = result.PromptTokens;
                // Both facts lived in OrchestratorResult and were read by nobody: a run cut short at
                // its iteration limit returned a fluent answer, indistinguishable from a task
                // carried to its end. Symmetric with the Visual Studio window.
                var endNotice = result.ReachedIterationLimit ? Strings.AgentEndedAtIterationLimit
                              : result.WasLoopDetected       ? Strings.AgentEndedOnRepeat
                              : null;
                return new ChatSendResult(
                    FinalAnswer(result.FinalResponse, streamed.ToString(), result.Executions, model, s),
                    false, result.TokensUsed, result.PromptTokens, EndNotice: endNotice);
            }

            var turn = await s.Client.SendChatAsync(
                model, s.History, EmptyToolRegistry.Instance, OnToken, cts.Token, onThinking: OnThinking);
            s.History.Add(new ChatMessageDto("assistant", turn.TextContent));
            s.LastPromptTokens = turn.PromptTokens;
            return new ChatSendResult(
                FinalAnswer(turn.TextContent, streamed.ToString(), [], model, s),
                false, turn.TokensUsed, turn.PromptTokens);
        }
        catch (OperationCanceledException)
        {
            return new ChatSendResult(streamed.ToString(), true, 0, 0);
        }
        catch (Exception ex)
        {
            // Plain-chat network failures (the agent loop never throws them) become a
            // structured error the adapter can render, not a generic RPC fault.
            Diagnostics.Swallow("HostServer.ChatSend", ex);
            return new ChatSendResult(streamed.ToString(), false, 0, 0, ex.Message);
        }
        finally
        {
            // Mirrors the VS window: the tracking run opened for this turn is closed here,
            // otherwise a later write (a /restore, a tool launched by a slash command) still
            // attaches to it and /undo-run reverts that write along with the turn we just watched.
            s.Tools.History.EndRun();   // `s`, not Session(): nothing may throw inside this finally
            ReleaseTurn(cts);
        }
    }

    [JsonRpcMethod("chat/cancel")]
    public void ChatCancel() { lock (_gate) _chatCts?.Cancel(); }

    /// <summary>Releases a step-mode pause, letting the agent proceed to its next action.</summary>
    [JsonRpcMethod("chat/resumeStep")]
    public void ChatResumeStep() => Session().StepResume?.TrySetResult(true);

    /// <summary>Step-mode pause: notifies the adapter (`chat/stepPaused`) and waits for
    /// `chat/resumeStep` — mirror of the VS VM's <c>PauseForStepAsync</c>.</summary>
    private async Task PauseForStepAsync(HostSession s, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.StepResume = tcs;
        Notify("chat/stepPaused", new { });
        try
        {
            await tcs.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Ensure the TCS is completed so any racing awaiter unblocks cleanly.
            tcs.TrySetCanceled(ct);
            throw;
        }
        finally
        {
            s.StepResume = null;
            Notify("chat/stepResumed", new { });
        }
    }

    [JsonRpcMethod("chat/reset")]
    public void ChatReset()
    {
        var s = Session();
        WithTurnSlot("chat/reset", () => ResetHistory(s));
    }

    /// <summary>Full slash-command list for the adapter's autocomplete popup: built-ins (hints
    /// localized by the `initialize` locale handshake) then user templates — the same sources
    /// as the VS autocomplete (<see cref="SlashCommandRouter.MatchCommands"/>).</summary>
    [JsonRpcMethod("command/list")]
    public List<SlashCommandInfoDto> CommandList()
    {
        var s = Session();
        var templates = SlashTemplates.Load(s.Config, string.IsNullOrEmpty(s.RootDir) ? null : s.RootDir);
        return SlashCommandRouter.BuiltInCommands
            .Concat(templates.Select(t => (Cmd: t.Name, Hint: SlashTemplates.HintOf(t))))
            .Select(c => new SlashCommandInfoDto(c.Cmd, c.Hint))
            .ToList();
    }

    /// <summary>
    /// Runs an in-place code action headlessly (same pipeline as the VS commands) and returns
    /// the rewrite as independent per-hunk edits — the adapter previews/applies them natively
    /// (VS Code: WorkspaceEdit + Refactor Preview). Never applies anything host-side.
    /// </summary>
    [JsonRpcMethod("codeAction/run", UseSingleObjectParameterDeserialization = true)]
    public async Task<CodeActionResultDto> CodeActionRunAsync(CodeActionParams p, CancellationToken ct)
    {
        var s = Session();
        var (system, instruction) = p.Kind switch
        {
            "fix"      => (InPlaceCodeActionPrompts.FixSystem,       InPlaceCodeActionPrompts.FixInstruction),
            "refactor" => (InPlaceCodeActionPrompts.RefactorSystem,  InPlaceCodeActionPrompts.RefactorInstruction),
            "doc"      => (InPlaceCodeActionPrompts.DocstringSystem, InPlaceCodeActionPrompts.DocstringInstruction),
            _          => throw new ArgumentException($"Unknown code action kind '{p.Kind}'."),
        };

        var model = string.IsNullOrWhiteSpace(p.Model)
            ? ModelRouter.Resolve(s.Config, ModelRole.CodeActions)
            : p.Model!;
        var run   = await CodeActionPipeline.RunAsync(
            s.Client, model, system, instruction,
            p.Text, p.SelStart, p.SelEnd, selectionEmpty: p.SelStart == p.SelEnd, ct);

        if (run.Outcome == CodeActionOutcome.NoChangeNeeded) return new("noChange", []);
        if (run.Outcome != CodeActionOutcome.Edited)         return new("failed",   [], FailureDetail: run.FailureDetail);

        var edits = InlineDiffPlanner.ToEdits(InlineDiffPlanner.Plan(p.Text, run.NewDocText!))
            .Select(e => new CodeActionEditDto(e.Index, e.Start, e.End, e.NewText))
            .ToList();

        // The model rewrote the code identically — treat as a no-op rather than an empty preview.
        return edits.Count == 0
            ? new CodeActionResultDto("noChange", [])
            : new CodeActionResultDto("edited", edits, run.NewDocText);
    }

    /// <summary>
    /// Renders the export document - the SAME one the Visual Studio window produces, because it is
    /// the same <see cref="ConversationExporter"/>. The adapter picks the format (the extension of
    /// the file the user named) and supplies its bubbles; the model and the date come from here.
    /// </summary>
    [JsonRpcMethod("chat/export", UseSingleObjectParameterDeserialization = true)]
    public Task<string> ChatExportAsync(ChatExportParams p, CancellationToken ct)
    {
        var s = Session();

        // Same filter as the VM: status and error bubbles are not turns.
        var messages = (p.Messages ?? [])
            .Where(m => m.Role is "user" or "assistant" or "tool")
            .Select(m => new ExportMessage(m.Role,
                                           ConversationExporter.RoleLabel(m.Role, m.Name),
                                           m.Content ?? string.Empty,
                                           m.Timestamp ?? string.Empty))
            .ToList();

        var duration = p.DurationSeconds is int seconds && seconds > 0
            ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;

        return Task.FromResult(ConversationExporter.Build(
            messages, p.AsPlainText, s.Config.DefaultModel, p.SessionTokens,
            DateTime.Now.ToString("f", CultureInfo.CurrentCulture),
            ConversationExporter.FormatDuration(duration)));
    }

    // ── Backend ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists the models of the backend the panel HAS IN FRONT OF IT, not of the saved one: the
    /// three values come from the form when it supplies them, from the configuration otherwise
    /// (the refresh button of an ordinary session, or the first load).
    /// </summary>
    /// <remarks>
    /// ⚠ Only ever read the session. The VS Code panel therefore offered a refresh that listed the
    /// models of the OLD url after you typed a new one - the other half of the Test button's
    /// defect, same class: <i>the panel acts on what is saved while the user is looking at what
    /// they typed</i>. The Visual Studio window has always built a throwaway
    /// <see cref="InferpalConfig"/> from its form values (<c>RefreshModelsAsync</c>); this does
    /// exactly the same.
    /// </remarks>
    [JsonRpcMethod("models/list", UseSingleObjectParameterDeserialization = true)]
    public async Task<IReadOnlyList<string>> ListModelsAsync(ModelsListParams p, CancellationToken ct)
    {
        var s = Session();
        if (string.IsNullOrWhiteSpace(p.BaseUrl) && string.IsNullOrWhiteSpace(p.Provider)
                                                 && string.IsNullOrWhiteSpace(p.ApiKey))
            return await s.Client.ListModelsAsync(ct);

        var url   = string.IsNullOrWhiteSpace(p.BaseUrl) ? s.Config.BaseUrl : p.BaseUrl.Trim();
        var draft = new InferpalConfig
        {
            Provider = string.IsNullOrWhiteSpace(p.Provider) ? s.Config.Provider : p.Provider.Trim(),
            BaseUrl  = url,
            ApiKey   = p.ApiKey ?? s.Config.ApiKey,
        };
        return await _providerFactory(draft).ListModelsAsync(ct, url);
    }

    /// <summary>
    /// Probes the url the panel gives it - the form's - and NAMES the backend that answered. Mirror
    /// of the Visual Studio window's Test button.
    /// </summary>
    /// <remarks>
    /// ⚠ Used to probe <c>Config.BaseUrl</c>, the saved url, ignoring the one the webview was
    /// sending: "Connected" could therefore be about an address other than the one displayed. See
    /// <see cref="ConnectionCheckResult"/>.
    /// <para>
    /// The probe is <see cref="ProviderProbe"/>, not <c>CheckConnectionAsync</c>: it requires the
    /// root property that signs the backend, so a positive answer proves reachability AND identity
    /// at once - the same reasoning as the VS window, and what lets us pre-select the right
    /// provider instead of leaving the user to guess.
    /// </para>
    /// </remarks>
    [JsonRpcMethod("connection/check", UseSingleObjectParameterDeserialization = true)]
    public async Task<ConnectionCheckResult> CheckConnectionAsync(ConnectionCheckParams p, CancellationToken ct)
    {
        var s   = Session();
        var url = string.IsNullOrWhiteSpace(p.BaseUrl) ? s.Config.BaseUrl : p.BaseUrl.Trim();

        var detected = await ProviderProbe.DetectAsync(url, s.Config.ApiKey, ct);
        return new ConnectionCheckResult(detected is not null, detected);
    }

    /// <summary>Connection badge for the adapter's header: reachability + the compact VRAM line
    /// (only when the provider supports <c>/api/ps</c>). Best-effort — failures degrade to
    /// "unreachable", never an RPC fault (this is polled).</summary>
    [JsonRpcMethod("backend/status")]
    public async Task<BackendStatusResult> BackendStatusAsync(CancellationToken ct)
    {
        var s = Session();
        var connected = false;
        try { connected = await s.Client.CheckConnectionAsync(s.Config.BaseUrl, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Diagnostics.Swallow("HostServer.BackendStatus", ex); }

        var badge = string.Empty;
        if (connected && s.Client.Capabilities.VramMonitoring)
        {
            // /api/ps is a plain HTTP call, not gated by the GPU scheduler — safe during a turn.
            try { badge = ModelCatalog.FormatVramBadge(await s.Client.GetRunningModelsAsync(ct)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Diagnostics.Swallow("HostServer.BackendStatus", ex); }
        }
        // ⚠ The EDGE, not the state. A dot that changes colour says "this is how it is now"; it
        // does not say "it just dropped", and that sentence is what the Visual Studio window has
        // always put in the thread. The decision belongs to the Core: first check silent if it
        // succeeds, announced if it fails, and nothing at all while nothing moves.
        var status = s.Connection.Evaluate(connected);
        var notice = status.Transition switch
        {
            ConnectionTransition.Restored => Strings.MsgHeartbeatRestored,
            ConnectionTransition.Lost     => Strings.MsgConnectionGuardFailed(
                                                 s.Config.BaseUrl,
                                                 InferenceProviderFactory.DisplayName(s.Config.Provider)),
            _                             => null,
        };
        return new BackendStatusResult(connected, badge, notice);
    }

    [JsonRpcMethod("fim/complete", UseSingleObjectParameterDeserialization = true)]
    public async Task<string> FimCompleteAsync(FimParams p, CancellationToken ct)
    {
        var s  = Session();
        var sb = new StringBuilder();
        await s.Client.StreamFimAsync(p.Prefix, p.Suffix, p.MaxTokens, p.Temperature,
                                      t => sb.Append(t), ct, p.Model);
        return sb.ToString();
    }

    // ── Config ─────────────────────────────────────────────────────────────────

    [JsonRpcMethod("config/get")]
    public string ConfigGet()
        => JsonSerializer.Serialize(Session().Config, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// Replaces the whole config (round-trip of `config/get`: absent fields reset to their
    /// defaults). Values are copied onto the shared instance so every live service sees them;
    /// switching <c>Provider</c>/<c>BaseUrl</c> still requires a new `initialize`.
    /// </summary>
    [JsonRpcMethod("config/update", UseSingleObjectParameterDeserialization = true)]
    public ConfigUpdateResult ConfigUpdate(ConfigUpdateParams p)
    {
        var s        = Session();
        var incoming = JsonSerializer.Deserialize<InferpalConfig>(p.Json)
                       ?? throw new ArgumentException("Invalid config JSON.");

        // Slot-held, not merely idle-checked: a settings save (or onDidChangeConfiguration) used
        // to mutate the shared Config the agent loop reads and replace the History it appends to,
        // mid-run (pre-1.6.0 architecture review, §2.6).
        WithTurnSlot("config/update", () =>
        {
            foreach (var prop in typeof(InferpalConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (prop.CanRead && prop.CanWrite)
                    prop.SetValue(s.Config, prop.GetValue(incoming));

            s.Config.Save();
            // A language override takes effect immediately (settings/strings, slash hints, …), like the
            // VS settings window; going back to "Auto" returns to the editor's locale rather than
            // leaving whatever was last applied.
            ApplyLanguage(s.Config);
            ResetHistory(s);   // custom prompt / pinned files may have changed
        });

        // After the save, on the configuration that was kept: a rule the product could not read is
        // not lost input - the field IS saved - but a protection the user believes they put in
        // place and that does not apply.
        Services.Execution.PermissionPolicy.ParseRules(s.Config.PermissionRules, out var dropped);
        return new ConfigUpdateResult(dropped.Count);
    }

    // ── RAG index ──────────────────────────────────────────────────────────────

    [JsonRpcMethod("index/start")]
    public void IndexStart()
    {
        var s = Session();
        s.Index.StartIndexing(s.RootDir);
    }

    [JsonRpcMethod("index/status")]
    public IndexStatusResult IndexStatus()
    {
        var s = Session();
        return new IndexStatusResult(s.Index.IsIndexing, s.Index.ChunkCount, s.Index.RootDir);
    }

    // ── Sessions (persisted store shared with the VS extension) ───────────────

    [JsonRpcMethod("session/save", UseSingleObjectParameterDeserialization = true)]
    public Task SessionSaveAsync(SessionSaveParams p, CancellationToken ct)
    {
        var s = Session();
        // The auto-save slot is not "the file this conversation lives in" (see CurrentSessionName).
        if (p.Name != "last_session") s.CurrentSessionName = p.Name;
        return s.Store.SaveAsync(p.Name, p.Messages.Select(ToSaved), ct,
                                 workspaceRoot: p.Name == "last_session" ? s.RootDir : null);
    }

    /// <summary>Wire message → stored message (empty optional fields stay out of the JSON).</summary>
    private static SavedMessage ToSaved(SavedMessageDto m) => new(
        m.Role, m.Content,
        string.IsNullOrEmpty(m.ToolName)  ? null : m.ToolName,
        string.IsNullOrEmpty(m.Timestamp) ? null : m.Timestamp);

    [JsonRpcMethod("session/list")]
    public async Task<List<SessionSummaryDto>> SessionListAsync(CancellationToken ct)
    {
        var summaries = await Session().Store.ListWithPreviewAsync(ct);
        return summaries
            .Select(x => new SessionSummaryDto(x.Name, x.SavedAt, x.MessageCount, x.FirstUserPreview, x.Parent, x.ForkTurn))
            .ToList();
    }

    /// <summary>Loads a session: the host history is rebuilt (fresh system prompt + every
    /// conversational turn, tool results included as plain labelled turns — see
    /// <c>SessionManager.BuildRestoredHistory</c>) and the transcript is returned for
    /// re-rendering. Null when the session doesn't exist.</summary>
    [JsonRpcMethod("session/load", UseSingleObjectParameterDeserialization = true)]
    public Task<SessionLoadResult?> SessionLoadAsync(SessionRefParams p, CancellationToken ct) =>
        // Slot held across the await: the old entry-check let a chat/send slip in while the store
        // was loading, then the history swap landed under the running loop (revue §2.6, TOCTOU).
        WithTurnSlotAsync<SessionLoadResult?>("session/load", ct, async token =>
        {
            var s    = Session();
            var data = await s.Store.LoadAsync(p.Name, token);
            if (data is null) return null;
            // The auto-save slot is one file for every project: another workspace's conversation
            // is not this one's to restore.
            if (p.Name == "last_session" && !SessionManager.AutoSaveBelongsHere(data, s.RootDir)) return null;

            s.History            = SessionManager.BuildRestoredHistory(BuildSystemPromptText(s), data.Messages);
            s.CurrentSessionName = p.Name == "last_session" ? null : p.Name;
            return new SessionLoadResult(
                p.Name,
                data.Messages.Select(m => new SavedMessageDto(m.Role, m.Content, m.ToolName, m.Timestamp)).ToList());
        });

    [JsonRpcMethod("session/delete", UseSingleObjectParameterDeserialization = true)]
    public bool SessionDelete(SessionRefParams p) => Session().Store.Delete(p.Name);

    /// <summary>
    /// Forks the conversation at <paramref name="p"/>.Turn (<c>/branch &lt;n&gt;</c>, ROADMAP 1.4.0 §7):
    /// the branch keeps turns 1..Turn, records its parent, becomes the current session and its
    /// (truncated) history replaces the host's. The parent is written to disk first when the
    /// conversation had no file yet — branching must not lose the discarded half. Null when the
    /// turn doesn't exist.
    /// </summary>
    [JsonRpcMethod("session/branch", UseSingleObjectParameterDeserialization = true)]
    public Task<SessionBranchResult?> SessionBranchAsync(SessionBranchParams p, CancellationToken ct) =>
        // Slot held across the awaits — same TOCTOU as session/load (revue §2.6).
        WithTurnSlotAsync<SessionBranchResult?>("session/branch", ct, async token =>
        {
            var s        = Session();
            var messages = p.Messages.Select(ToSaved).ToList();
            var sessions = await s.Store.ListWithPreviewAsync(token);

            var plan = BranchManager.Plan(messages, p.Turn, s.CurrentSessionName, sessions, DateTime.Now);
            if (plan is null) return null;

            // The parent is rewritten with the conversation as it stands now (it may have moved on
            // since it was loaded), keeping its own parent link when it is itself a branch.
            await s.Store.SaveAsync(plan.ParentName, plan.ParentMessages, token,
                                    parent: plan.ParentParent, forkTurn: plan.ParentForkTurn);

            await s.Store.SaveAsync(plan.BranchName, plan.BranchMessages, token,
                                    parent: plan.ParentName, forkTurn: plan.ForkTurn);

            s.History            = SessionManager.BuildRestoredHistory(BuildSystemPromptText(s), plan.BranchMessages);
            s.CurrentSessionName = plan.BranchName;

            return new SessionBranchResult(
                plan.BranchName, plan.ParentName, plan.ForkTurn,
                plan.BranchMessages.Select(m => new SavedMessageDto(m.Role, m.Content, m.ToolName, m.Timestamp)).ToList(),
                Strings.BranchCreated(plan.BranchName, plan.ForkTurn, plan.ParentName));
        });

    /// <summary>
    /// LLM-generated name for a conversation (utility model via the Model Router) — the VS Code
    /// counterpart of what the VS VM does when <c>/clear</c> archives a session. Falls back to a
    /// snippet of the message when the backend can't answer, so it always returns something usable.
    /// <paramref name="p"/>.Text is optional: empty means "the first user message of this session".
    /// </summary>
    [JsonRpcMethod("session/title", UseSingleObjectParameterDeserialization = true)]
    public async Task<SessionTitleResult> SessionTitleAsync(SessionTitleParams p, CancellationToken ct)
    {
        var s    = Session();
        var text = !string.IsNullOrWhiteSpace(p.Text)
            ? p.Text!
            : s.History.FirstOrDefault(m => m.Role == "user")?.Content ?? string.Empty;

        var title = await SessionTitleGenerator.GenerateAsync(s.Client, s.Config, text, ct);
        return new SessionTitleResult(title, SessionManager.SessionFileName(DateTime.Now, title));
    }

    // ── Context X-Ray panel (interactive /xray V2) ─────────────────────────────

    /// <summary>Full panel model (sections, totals, warning, raw prompt) for the webview.</summary>
    [JsonRpcMethod("xray/panel")]
    public XRayPanelDto XrayPanel() => ToXRayPanelDto(Session());

    /// <summary>Switches one prompt section on/off for the session's next turns, refreshes the
    /// in-memory system prompt and returns the updated panel model.</summary>
    [JsonRpcMethod("xray/toggle", UseSingleObjectParameterDeserialization = true)]
    public XRayPanelDto XrayToggle(XRayToggleParams p)
    {
        var s = Session();
        // Slot-held: this rewrites History[0] — under a running loop that is the same race as
        // chat/reset (revue §2.6).
        return WithTurnSlotFunc("xray/toggle", () =>
        {
            if (p.Enabled) s.XrayDisabledSections.Remove(p.Id);
            else           s.XrayDisabledSections.Add(p.Id);

            if (s.History.Count > 0 && s.History[0].Role == "system")
                s.History[0] = new ChatMessageDto("system", BuildSystemPromptText(s));

            return ToXRayPanelDto(s);
        });
    }

    private T WithTurnSlotFunc<T>(string operation, Func<T> body)
    {
        var cts = AcquireTurnFor(operation);
        try { return body(); }
        finally { ReleaseTurn(cts); }
    }

    // ── Open-document overlay & active editor (notifications from the adapter) ─

    [JsonRpcMethod("textDocument/didOpen", UseSingleObjectParameterDeserialization = true)]
    public void DidOpen(DocumentParams p) => Session().Overlay.Set(p.Path, p.Text ?? string.Empty);

    [JsonRpcMethod("textDocument/didChange", UseSingleObjectParameterDeserialization = true)]
    public void DidChange(DocumentParams p) => Session().Overlay.Set(p.Path, p.Text ?? string.Empty);

    [JsonRpcMethod("textDocument/didClose", UseSingleObjectParameterDeserialization = true)]
    public void DidClose(DocumentParams p) => Session().Overlay.Remove(p.Path);

    [JsonRpcMethod("editor/didChangeActiveDocument", UseSingleObjectParameterDeserialization = true)]
    public void DidChangeActiveDocument(DocumentParams p)
        => Session().Editor.SetActiveDocument(string.IsNullOrEmpty(p.Path) ? null : p.Path);

    // ── Internals ──────────────────────────────────────────────────────────────

    private HostSession Session()
        => _session ?? throw new InvalidOperationException("Call 'initialize' first.");

    /// <summary>Claims the single-turn slot (chat turn or long slash command); throws when one
    /// is already running. The returned CTS is cancelled by `chat/cancel` and `shutdown`.</summary>
    private CancellationTokenSource AcquireTurn(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_chatCts is not null)
                throw new InvalidOperationException("A chat turn is already running.");
            return _chatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }
    }

    private void ReleaseTurn(CancellationTokenSource cts)
    {
        lock (_gate) _chatCts = null;
        cts.Dispose();
    }

    /// <summary>
    /// Runs a History/Config mutation while <b>holding</b> the turn slot. The predecessor
    /// (`AssertIdle`) merely tested the slot at entry — a check-then-act: a `chat/send` arriving
    /// during the operation's awaits slipped in and raced the very mutation the check existed to
    /// prevent (pre-1.6.0 architecture review, §2.6). Taking the slot makes the exclusion symmetric: the
    /// mutation excludes a turn exactly as a turn excludes the mutation.
    /// </summary>
    private void WithTurnSlot(string operation, Action body)
    {
        var cts = AcquireTurnFor(operation);
        try { body(); }
        finally { ReleaseTurn(cts); }
    }

    /// <inheritdoc cref="WithTurnSlot"/>
    private async Task<T> WithTurnSlotAsync<T>(string operation, CancellationToken ct, Func<CancellationToken, Task<T>> body)
    {
        var cts = AcquireTurnFor(operation, ct);
        try { return await body(cts.Token); }
        finally { ReleaseTurn(cts); }
    }

    private CancellationTokenSource AcquireTurnFor(string operation, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_chatCts is not null)
                throw new InvalidOperationException(
                    $"'{operation}' cannot run while a chat turn is in flight — cancel it first.");
            return _chatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }
    }

    /// <summary>Per-turn RAG auto-context (mirror of the VS VM's <c>BuildAutoContextAsync</c>):
    /// warm shadow result when available, else one bounded embed + search. Best-effort —
    /// any failure yields no auto-context, never an error.</summary>
    private static async Task<string> BuildRagAutoContextAsync(
        HostSession s, string userText, List<string>? attachedPaths, CancellationToken ct)
    {
        if (!s.Config.RagAutoContextEnabled || !s.Config.RagEnabled)   return string.Empty;
        if (s.Index.ChunkCount == 0 || s.Client.IsEmbeddingCircuitOpen) return string.Empty;

        var trimmed = userText.Trim();
        if (trimmed.Length < 12 || trimmed.StartsWith('/')) return string.Empty;   // same gate as VS

        try
        {
            var (_, results) = s.Index.TryGetShadow(trimmed);
            if (results is null || results.Count == 0)
            {
                var model = string.IsNullOrEmpty(s.Config.RagEmbeddingModel)
                    ? "nomic-embed-text" : s.Config.RagEmbeddingModel;
                var embedding = await s.Client.GetEmbeddingAsync(trimmed, model, ct);
                if (embedding is null) return string.Empty;
                results = await s.Index.SearchAsync(embedding, trimmed, RagAutoContext.DefaultMaxChunks, ct);
            }
            if (results is null or { Count: 0 }) return string.Empty;

            // Parity with the VS VM: the adapter names the files it inlined as attachments so a
            // chunk of an attached file is not injected a second time — this set used to be
            // hard-coded empty (pre-1.6.0 architecture review). Resolved to full paths, the grain the
            // chunks carry.
            var attached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (attachedPaths is { Count: > 0 } && !string.IsNullOrEmpty(s.RootDir))
                foreach (var p in attachedPaths)
                    try { attached.Add(Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(s.RootDir, p))); }
                    catch (Exception ex) { Diagnostics.Swallow("BuildRagAutoContext.AttachedPath", ex); }

            return RagAutoContext.Build(results, attached);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("HostServer.RagAutoContext", ex);
            return string.Empty;
        }
    }

    /// <summary>Layered system prompt (same builder as the VS VM), honouring the sections
    /// switched off from the Context X-Ray panel, the active `/template` suffix and the
    /// plan-mode instructions.</summary>
    private static string BuildSystemPromptText(HostSession s)
    {
        var prompt = new SystemPromptBuilder(s.Config, EditorName).Build(
            Strings.SystemPrompt,
            projectRoot: string.IsNullOrEmpty(s.RootDir) ? null : s.RootDir,
            disabledSectionIds: s.XrayDisabledSections);
        if (!string.IsNullOrEmpty(s.TemplateSuffix)) prompt += "\n\n" + s.TemplateSuffix;
        if (s.PlanMode)                              prompt += PlanModeToolRegistry.SystemPromptSuffix;
        return prompt;
    }

    /// <summary>Prompt layers for the X-Ray panel — same inputs as <see cref="BuildSystemPromptText"/>.</summary>
    private static IReadOnlyList<PromptSection> BuildPromptSections(HostSession s) =>
        new SystemPromptBuilder(s.Config, EditorName).BuildSections(
            Strings.SystemPrompt,
            projectRoot: string.IsNullOrEmpty(s.RootDir) ? null : s.RootDir);

    private static XRayPanelDto ToXRayPanelDto(HostSession s)
    {
        var model = XRayPanelPresenter.Build(
            BuildPromptSections(s), s.XrayDisabledSections,
            AgentOrchestrator.EstimateTokens(SnapshotHistory(s)), s.Config.ContextWindowSize);
        return new XRayPanelDto(
            model.Sections.Select(x => new XRaySectionDto(
                x.Id, x.Label, x.Tokens, x.Percent, x.Content, x.Enabled, x.CanToggle)).ToList(),
            model.TotalTokens, model.HistoryTokens, model.ContextWindow,
            model.FillPercent, model.OverheadWarning, model.RawPrompt);
    }

    /// <summary>Bounded defensive copy of the history: `xray/panel` is a read the webview can ask
    /// for mid-stream (its button is not gated on busy), and enumerating the List the agent loop
    /// is appending to throws (pre-1.6.0 architecture review, §2.6). A handful of retries always wins — the
    /// loop appends in bursts, it does not spin.</summary>
    private static List<ChatMessageDto> SnapshotHistory(HostSession s)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return s.History.ToList(); }
            catch (InvalidOperationException) when (attempt < 5) { }  // modified during enumeration
            catch (ArgumentException)         when (attempt < 5) { }  // resized during CopyTo
        }
    }

    /// <summary>Reseeds the history with the layered system prompt.</summary>
    private static void ResetHistory(HostSession s)
    {
        s.History            = [new ChatMessageDto("system", BuildSystemPromptText(s))];
        s.CurrentSessionName = null;   // the archived conversation keeps its own file
    }

    /// <summary>VS Code locale ids are lowercase (`zh-cn`); .NET wants `zh-CN`. GetCultureInfo
    /// is case-insensitive, so validating through it normalizes; invalid ⇒ null (OS culture).</summary>
    /// <summary>
    /// Applies the language with the only precedence that makes sense here: an explicit choice in
    /// the settings, else the locale the editor announced, else the machine's UI culture.
    /// </summary>
    private void ApplyLanguage(InferpalConfig config) =>
        Strings.ApplyLanguage(string.IsNullOrEmpty(config.Language) ? _editorLocale : config.Language);

    private static string? NormalizeLocale(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return null;
        try { return CultureInfo.GetCultureInfo(locale).Name; }
        catch (CultureNotFoundException) { return null; }
    }

    /// <summary>Fire-and-forget notification; a dead connection is traced, never thrown.</summary>
    private void Notify(string method, object payload)
    {
        try
        {
            var send = _rpc?.NotifyWithParameterObjectAsync(method, payload);
            _ = send?.ContinueWith(
                t => Diagnostics.Swallow("HostServer.Notify", t.Exception!),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception ex) { Diagnostics.Swallow("HostServer.Notify", ex); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _chatCts?.Cancel();
            _chatCts = null;
        }
        _session?.Dispose();
        _session = null;
    }

    /// <summary>
    /// What the turn actually puts on screen: the text, else the summary of the tools that ran,
    /// else a message that NAMES what was observed.
    /// </summary>
    /// <remarks>
    /// This fallback chain lived only in the Visual Studio window. On the VS Code side a turn with
    /// no text ended <b>in silence</b>: no answer, no message, no explanation - exactly the defect
    /// 1.6.8 led with, repaired on one side only. The diagnostic half is in the Core
    /// (<c>OpenAiCompatibleClient</c> records every empty turn in <c>/diagnostics</c>) so both
    /// editors already had it; it is the VISIBLE half that was missing.
    ///
    /// The decision is <see cref="ChatTurnPolicy.DecideFinalAnswer"/>, the same one the VM uses -
    /// not a second implementation.
    /// </remarks>
    private static string FinalAnswer(
        string? finalResponse, string streamed, IReadOnlyList<ToolExecution> executions,
        string model, HostSession s) =>
        ChatTurnPolicy.DecideFinalAnswer(
            streamingBubbleVisible: MarkdownParser.HasPrintableText(streamed),
            finalResponse:          finalResponse,
            executionCount:         executions.Count) switch
        {
            // The stream already said everything: the adapter renders `text || streamText`.
            FinalAnswerKind.StreamedAnswer => finalResponse ?? string.Empty,
            FinalAnswerKind.FinalText      => finalResponse ?? string.Empty,
            FinalAnswerKind.ToolSummary    => Strings.MsgAgentDone(ChatTurnPolicy.BuildToolSummary(executions)),
            _                              => Strings.MsgEmptyResponseFrom(model, s.Client.ServerAddress),
        };
}
