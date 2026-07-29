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
    private readonly Func<InferpalConfig, IInferenceProvider> _providerFactory;
    private readonly Func<InferpalConfig> _configFactory;
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();

    private JsonRpc?                 _rpc;
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
        Strings.ApplyLanguage(NormalizeLocale(p.Locale));

        var config   = _configFactory();
        var client   = _providerFactory(config);
        var overlay  = new OpenDocumentOverlay();
        var editor   = new RpcEditorSurface(rpc, overlay);
        var approval = new RpcApprovalService(config, () => p.RootDir, rpc);
        var lsp      = new LspSemanticProvider();
        var index    = new ProjectIndexService(client, config, lsp);
        var mcp      = new McpToolService(config, approval);
        var docs     = new DocsIndexService(client, config);
        var tools    = new ToolRegistry(editor, approval, config, index, client,
                                        new ProjectMapService(editor), mcp, docs, overlay);

        // Pin the file-tool confinement root even when RAG never indexes; the adapter
        // opts into indexing explicitly via `index/start` (VS Code shows its own gate).
        index.SetRoot(p.RootDir);

        _session?.Dispose();
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
        };
        ResetHistory(_session);

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
        void OnThinking(string t) => Notify("chat/thinking", new { text = t });

        try
        {
            s.History.Add(new ChatMessageDto("user", p.Prompt));
            // The session-scoped `/tools off` switch forces plain chat, like the VS VM.
            var agentMode = (p.AgentMode ?? s.Config.AgentModeEnabled) && s.ToolsEnabled;
            // An explicit per-request model always wins; otherwise the Model Router applies the
            // same role chains as the VS adapter (agent loop → AgentModel, plain chat → DefaultModel).
            var model     = !string.IsNullOrWhiteSpace(p.Model)
                ? p.Model!
                : ModelRouter.Resolve(s.Config, agentMode ? ModelRole.Agent : ModelRole.Chat);

            if (agentMode)
            {
                // Group this run's file snapshots so the adapter can offer /undo-run semantics.
                s.Tools.History.BeginRun();
                var result = await s.Orchestrator.RunAsync(
                    model, s.History, s.Tools,
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

                s.History = result.UpdatedHistory;
                return new ChatSendResult(result.FinalResponse, false, result.TokensUsed, result.PromptTokens);
            }

            var turn = await s.Client.SendChatAsync(
                model, s.History, EmptyToolRegistry.Instance, OnToken, cts.Token, onThinking: OnThinking);
            s.History.Add(new ChatMessageDto("assistant", turn.TextContent));
            return new ChatSendResult(turn.TextContent, false, turn.TokensUsed, turn.PromptTokens);
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
            ReleaseTurn(cts);
        }
    }

    [JsonRpcMethod("chat/cancel")]
    public void ChatCancel() { lock (_gate) _chatCts?.Cancel(); }

    [JsonRpcMethod("chat/reset")]
    public void ChatReset() => ResetHistory(Session());

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

    // ── Backend ────────────────────────────────────────────────────────────────

    [JsonRpcMethod("models/list")]
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
        => await Session().Client.ListModelsAsync(ct);

    [JsonRpcMethod("connection/check")]
    public Task<bool> CheckConnectionAsync(CancellationToken ct)
    {
        var s = Session();
        return s.Client.CheckConnectionAsync(s.Config.BaseUrl, ct);
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
        return new BackendStatusResult(connected, badge);
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
    public void ConfigUpdate(ConfigUpdateParams p)
    {
        var s        = Session();
        var incoming = JsonSerializer.Deserialize<InferpalConfig>(p.Json)
                       ?? throw new ArgumentException("Invalid config JSON.");

        foreach (var prop in typeof(InferpalConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (prop.CanRead && prop.CanWrite)
                prop.SetValue(s.Config, prop.GetValue(incoming));

        s.Config.Save();
        ResetHistory(s);   // custom prompt / pinned files may have changed
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
        var messages = p.Messages.Select(m => new SavedMessage(
            m.Role, m.Content,
            string.IsNullOrEmpty(m.ToolName)  ? null : m.ToolName,
            string.IsNullOrEmpty(m.Timestamp) ? null : m.Timestamp));
        return s.Store.SaveAsync(p.Name, messages, ct);
    }

    [JsonRpcMethod("session/list")]
    public async Task<List<SessionSummaryDto>> SessionListAsync(CancellationToken ct)
    {
        var summaries = await Session().Store.ListWithPreviewAsync(ct);
        return summaries
            .Select(x => new SessionSummaryDto(x.Name, x.SavedAt, x.MessageCount, x.FirstUserPreview))
            .ToList();
    }

    /// <summary>Loads a session: the host history is rebuilt (fresh system prompt + every
    /// conversational turn, tool results included) and the transcript is returned for
    /// re-rendering. Null when the session doesn't exist.</summary>
    [JsonRpcMethod("session/load", UseSingleObjectParameterDeserialization = true)]
    public async Task<SessionLoadResult?> SessionLoadAsync(SessionRefParams p, CancellationToken ct)
    {
        var s = Session();
        var data = await s.Store.LoadAsync(p.Name, ct);
        if (data is null) return null;

        s.History = SessionManager.BuildRestoredHistory(BuildSystemPromptText(s), data.Messages);
        return new SessionLoadResult(
            p.Name,
            data.Messages.Select(m => new SavedMessageDto(m.Role, m.Content, m.ToolName, m.Timestamp)).ToList());
    }

    [JsonRpcMethod("session/delete", UseSingleObjectParameterDeserialization = true)]
    public bool SessionDelete(SessionRefParams p) => Session().Store.Delete(p.Name);

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
        if (p.Enabled) s.XrayDisabledSections.Remove(p.Id);
        else           s.XrayDisabledSections.Add(p.Id);

        if (s.History.Count > 0 && s.History[0].Role == "system")
            s.History[0] = new ChatMessageDto("system", BuildSystemPromptText(s));

        return ToXRayPanelDto(s);
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

    /// <summary>Layered system prompt (same builder as the VS VM), honouring the sections
    /// switched off from the Context X-Ray panel and the active `/template` suffix.</summary>
    private static string BuildSystemPromptText(HostSession s)
    {
        var prompt = new SystemPromptBuilder(s.Config).Build(
            Strings.SystemPrompt,
            projectRoot: string.IsNullOrEmpty(s.RootDir) ? null : s.RootDir,
            disabledSectionIds: s.XrayDisabledSections);
        return string.IsNullOrEmpty(s.TemplateSuffix) ? prompt : prompt + "\n\n" + s.TemplateSuffix;
    }

    /// <summary>Prompt layers for the X-Ray panel — same inputs as <see cref="BuildSystemPromptText"/>.</summary>
    private static IReadOnlyList<PromptSection> BuildPromptSections(HostSession s) =>
        new SystemPromptBuilder(s.Config).BuildSections(
            Strings.SystemPrompt,
            projectRoot: string.IsNullOrEmpty(s.RootDir) ? null : s.RootDir);

    private static XRayPanelDto ToXRayPanelDto(HostSession s)
    {
        var model = XRayPanelPresenter.Build(
            BuildPromptSections(s), s.XrayDisabledSections,
            AgentOrchestrator.EstimateTokens(s.History), s.Config.ContextWindowSize);
        return new XRayPanelDto(
            model.Sections.Select(x => new XRaySectionDto(
                x.Id, x.Label, x.Tokens, x.Percent, x.Content, x.Enabled, x.CanToggle)).ToList(),
            model.TotalTokens, model.HistoryTokens, model.ContextWindow,
            model.FillPercent, model.OverheadWarning, model.RawPrompt);
    }

    /// <summary>Reseeds the history with the layered system prompt.</summary>
    private static void ResetHistory(HostSession s) =>
        s.History = [new ChatMessageDto("system", BuildSystemPromptText(s))];

    /// <summary>VS Code locale ids are lowercase (`zh-cn`); .NET wants `zh-CN`. GetCultureInfo
    /// is case-insensitive, so validating through it normalizes; invalid ⇒ null (OS culture).</summary>
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
}
