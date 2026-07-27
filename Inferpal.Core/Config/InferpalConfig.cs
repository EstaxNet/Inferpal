using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Inferpal.Localization;

namespace Inferpal.Config;

/// <summary>
/// All user-configurable settings, persisted as JSON at <c>%AppData%/Inferpal/config.json</c>.
/// </summary>
/// <remarks>
/// Load via <see cref="Load"/> at startup (also applies the saved language override).
/// Persist changes by calling <see cref="Save"/>.
/// </remarks>
internal class InferpalConfig
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Inferpal", "config.json");

    /// <summary>BCP-47 culture code for the UI language (e.g. <c>"fr"</c>, <c>"zh-CN"</c>). Empty string = follow VS.</summary>
    [JsonPropertyName("language")]
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// Inference backend: <c>"ollama"</c> (default, native Ollama API + full hardware-aware features)
    /// or <c>"openai-compatible"</c> (LM Studio, llama.cpp server, vLLM, Jan, LiteLLM… via the OpenAI
    /// <c>/v1</c> API). Resolved at startup by <see cref="Services.Inference.InferenceProviderFactory"/>;
    /// changing it takes effect on the next VS reload.
    /// </summary>
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "ollama";

    /// <summary>
    /// Base URL of the inference server. Ollama default: <c>http://localhost:11434</c>;
    /// LM Studio / OpenAI-compatible default: <c>http://localhost:1234</c> (the <c>/v1</c> suffix is
    /// appended automatically if omitted).
    /// </summary>
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Optional API key sent as <c>Authorization: Bearer</c> to OpenAI-compatible servers that require
    /// one. Ignored by the Ollama backend and by LM Studio (which needs no key). Empty = no auth header.
    /// </summary>
    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Model selected at startup; overridable at runtime with <c>/model &lt;name&gt;</c>.</summary>
    [JsonPropertyName("defaultModel")]
    public string DefaultModel { get; set; } = "llama3.1";

    /// <summary>Maximum seconds a <c>run_command</c> shell process may run before being killed (default: 120 = 2 min).</summary>
    [JsonPropertyName("commandTimeoutSeconds")]
    public int CommandTimeoutSeconds { get; set; } = 120;

    /// <summary>When <c>true</c>, tool call bubbles start expanded instead of collapsed.</summary>
    [JsonPropertyName("toolBubblesExpanded")]
    public bool ToolBubblesExpanded { get; set; } = false;

    /// <summary>When <c>true</c>, <c>write_file</c> and <c>run_command</c> skip the approval dialog.</summary>
    [JsonPropertyName("securityAlertsDisabled")]
    public bool SecurityAlertsDisabled { get; set; } = false;

    /// <summary>
    /// Total GPU VRAM budget in gigabytes, used by the hardware-aware features (<c>/hardware</c>,
    /// first-run trio fit-check). Ollama does not expose total VRAM and the server may be remote,
    /// so this is set manually (via <c>/hardware &lt;gb&gt;</c>) or auto-seeded only when Ollama runs
    /// on loopback. <c>0</c> = unknown → fit-checks stay silent.
    /// </summary>
    [JsonPropertyName("vramBudgetGb")]
    public double VramBudgetGb { get; set; } = 0;

    /// <summary>
    /// Context window size in tokens. Sent to Ollama as <c>num_ctx</c> and used as the
    /// client-side trim budget. Default 8192 keeps the KV-cache small enough to stay in
    /// VRAM (models otherwise default to very large windows, e.g. 256k, which spill to CPU).
    /// 0 = let Ollama use the model default and disable client-side trimming.
    /// </summary>
    [JsonPropertyName("contextWindowSize")]
    public int ContextWindowSize { get; set; } = 8192;

    /// <summary>Number of most-recent conversation turns to preserve when the context window is trimmed.</summary>
    [JsonPropertyName("contextWindowKeepTurns")]
    public int ContextWindowKeepTurns { get; set; } = 4;

    /// <summary>Text appended to the base system prompt before the project context. Supports any plain text.</summary>
    [JsonPropertyName("customSystemPrompt")]
    public string CustomSystemPrompt { get; set; } = string.Empty;

    /// <summary>
    /// Newline-separated list of absolute file paths (max 3) always injected into the system prompt.
    /// Each file's content is read on every request and prepended as a named context block.
    /// </summary>
    [JsonPropertyName("pinnedContextFiles")]
    public string PinnedContextFiles { get; set; } = string.Empty;

    /// <summary>
    /// User-defined slash command templates, one per line in the format <c>/name=text</c>.
    /// Use <c>{args}</c> as a placeholder for extra words typed after the command name.
    /// </summary>
    [JsonPropertyName("promptTemplates")]
    public string PromptTemplates { get; set; } = string.Empty;

    /// <summary>
    /// User-defined shell tools exposed to the agent, one per line: <c>name=powershell_command</c>.
    /// The tool name must be snake_case. The command runs in PowerShell with an optional
    /// <c>args</c> parameter appended when the model provides extra arguments.
    /// </summary>
    [JsonPropertyName("customTools")]
    public string CustomTools { get; set; } = string.Empty;

    /// <summary>
    /// Per-machine permission rules for tool approval, one per line in the DSL
    /// <c>allow|deny &lt;tool|*&gt; &lt;regex&gt;</c> (see <see cref="Services.Execution.PermissionPolicy"/>).
    /// An <c>allow</c> match auto-approves the call (no prompt); a <c>deny</c> match blocks it
    /// outright (even under <see cref="SecurityAlertsDisabled"/>); no match falls back to the
    /// interactive prompt. Evaluated before the workspace <c>.inferpal/permissions.json</c> overlay;
    /// first match wins. A built-in denylist of catastrophic shell commands always applies on top.
    /// </summary>
    [JsonPropertyName("permissionRules")]
    public string PermissionRules { get; set; } = string.Empty;

    /// <summary>
    /// When <c>true</c>, the system prompt is automatically enriched with a language-specific
    /// persona snippet when the active document changes (C#, Python, TypeScript, …).
    /// </summary>
    [JsonPropertyName("personaAutoSwitch")]
    public bool PersonaAutoSwitch { get; set; } = true;

    /// <summary>Every N conversation turns a session recap is generated and injected into the system prompt (0 = disabled).</summary>
    [JsonPropertyName("oodaTurnThreshold")]
    public int OodaTurnThreshold { get; set; } = 10;

    /// <summary>When <c>true</c>, old messages are summarized by the model instead of being hard-truncated.</summary>
    [JsonPropertyName("compactionEnabled")]
    public bool CompactionEnabled { get; set; } = true;

    /// <summary>Seconds before the compaction model call is cancelled and falls back to hard truncation (safety fuse).</summary>
    [JsonPropertyName("compactionTimeoutSeconds")]
    public int CompactionTimeoutSeconds { get; set; } = 45;

    /// <summary>
    /// Number of messages (after the system prompt) kept verbatim at the start of every compacted
    /// context, so Ollama can reuse its KV cache for those tokens.  0 = disabled.
    /// </summary>
    [JsonPropertyName("kvCacheAnchorMessages")]
    public int KvCacheAnchorMessages { get; set; } = 3;

    /// <summary>Inline completion performance preset: <c>"Fast"</c>, <c>"Default"</c>, or <c>"HighAccuracy"</c>.</summary>
    [JsonPropertyName("inlineCompletionMode")]
    public string InlineCompletionMode { get; set; } = "Default";

    /// <summary>When <c>false</c>, ghost-text completions are globally disabled.</summary>
    [JsonPropertyName("inlineCompletionEnabled")]
    public bool InlineCompletionEnabled { get; set; } = true;

    /// <summary>Model for Fill-in-the-Middle completions. Empty string = use <see cref="DefaultModel"/>.</summary>
    [JsonPropertyName("inlineCompletionModel")]
    public string InlineCompletionModel { get; set; } = string.Empty;

    /// <summary>Model for Explain/Fix/Refactor code actions. Empty string = use <see cref="DefaultModel"/>.</summary>
    [JsonPropertyName("codeActionsModel")]
    public string CodeActionsModel { get; set; } = string.Empty;

    /// <summary>
    /// Model for the Inline Edit (Ctrl+Shift+I) feature.
    /// Falls back to <see cref="CodeActionsModel"/>, then <see cref="DefaultModel"/>.
    /// </summary>
    [JsonPropertyName("inlineEditModel")]
    public string InlineEditModel { get; set; } = string.Empty;

    /// <summary>
    /// Model used by the autonomous agent (Plan → Act → Observe) loop, i.e. only when the Chat/Agent
    /// switch is on Agent. Empty string = use <see cref="DefaultModel"/>. Plain chat — even with
    /// tools enabled for the basic tool-calling loop — always stays on <see cref="DefaultModel"/>.
    /// A smaller, non-multimodal model with prompt-cache reuse (e.g. <c>qwen2.5-coder</c>) runs
    /// multi-turn tool loops far faster than a large multimodal model, which reprocesses the whole
    /// prompt every turn (LM Studio disables KV-cache reuse for multimodal models).
    /// </summary>
    [JsonPropertyName("agentModel")]
    public string AgentModel { get; set; } = string.Empty;

    // ── Smart Fix Protocol ────────────────────────────────────────────────────

    /// <summary>
    /// When <c>true</c>, a quick build / typecheck is triggered automatically after each
    /// <c>write_file</c> or <c>apply_diff</c> on a build-relevant file. The ecosystem is chosen from
    /// the file extension (built-in .NET / TypeScript / Rust / Go validators, extendable via the
    /// workspace <c>.inferpal/validators.json</c> overlay — see <see cref="Services.CodeActions.BuildValidators"/>).
    /// Compilation errors are returned inline so the agent can fix them in the same loop.
    /// </summary>
    [JsonPropertyName("smartFixEnabled")]
    public bool SmartFixEnabled { get; set; } = true;

    // ── RAG / Semantic indexing ────────────────────────────────────────────────

    /// <summary>
    /// When <c>true</c>, source files are indexed in the background and
    /// the <c>search_codebase</c> tool uses embedding-based semantic search.
    /// Set to <c>false</c> to disable all background indexing (keyword fallback still works).
    /// </summary>
    [JsonPropertyName("ragEnabled")]
    public bool RagEnabled { get; set; } = true;

    /// <summary>
    /// Ollama model used to generate embedding vectors for RAG indexing.
    /// Recommended: <c>nomic-embed-text</c> (768 dims) or <c>mxbai-embed-large</c> (1024 dims).
    /// Empty string falls back to <c>nomic-embed-text</c>.
    /// </summary>
    [JsonPropertyName("ragEmbeddingModel")]
    public string RagEmbeddingModel { get; set; } = string.Empty;

    /// <summary>
    /// Maximum number of chunks returned by a single <c>search_codebase</c> call (default 5, max 10).
    /// </summary>
    [JsonPropertyName("ragTopK")]
    public int RagTopK { get; set; } = 5;

    /// <summary>
    /// Minimum cosine similarity score [0–1] for a chunk to be included in semantic search results.
    /// Chunks below this threshold are discarded as low-quality matches (Global Priority Guard).
    /// Default 0.20 — conservative floor that cuts noise without hiding borderline-relevant code.
    /// </summary>
    [JsonPropertyName("ragSimilarityThreshold")]
    public float RagSimilarityThreshold { get; set; } = 0.20f;

    /// <summary>
    /// When <c>true</c>, each code-related chat turn silently retrieves the most relevant indexed
    /// chunks for the user's message and injects a small, budget-capped context block into the prompt
    /// (chunks already attached are skipped). Requires <see cref="RagEnabled"/> and a ready index.
    /// </summary>
    [JsonPropertyName("ragAutoContextEnabled")]
    public bool RagAutoContextEnabled { get; set; } = true;

    // ── MCP (Model Context Protocol) ──────────────────────────────────────────

    /// <summary>
    /// When <c>true</c>, configured MCP servers are spawned on startup and their tools
    /// are exposed to the agent. Default <c>false</c> — opt-in.
    /// </summary>
    [JsonPropertyName("mcpEnabled")]
    public bool McpEnabled { get; set; } = false;

    /// <summary>
    /// MCP server definitions as a JSON object keyed by server name, following the
    /// Claude Desktop / Continue convention:
    /// <code>{ "filesystem": { "command": "npx", "args": ["-y","@modelcontextprotocol/server-filesystem","C:\\dev"], "env": {} } }</code>
    /// Empty = no servers. Parsed by <see cref="Services.Mcp.McpServerConfig.Parse"/>.
    /// </summary>
    [JsonPropertyName("mcpServersJson")]
    public string McpServersJson { get; set; } = string.Empty;

    // ── @Docs (external documentation indexing) ───────────────────────────────

    /// <summary>
    /// Indexed external documentation sources as a JSON array, managed via the
    /// <c>/docs</c> slash command:
    /// <code>[ { "id": "react", "title": "React", "startUrl": "https://react.dev/learn" } ]</code>
    /// Each source is crawled (same-domain) and embedded so the <c>search_docs</c> tool can
    /// retrieve relevant passages. Parsed by <see cref="Services.Docs.DocSite.Parse"/>.
    /// Embeddings reuse <see cref="RagEmbeddingModel"/>.
    /// </summary>
    [JsonPropertyName("docSitesJson")]
    public string DocSitesJson { get; set; } = string.Empty;

    /// <summary>
    /// <c>true</c> until the first successful connection + model discovery runs.
    /// Reset to <c>false</c> permanently after first-run setup completes (even if no models found).
    /// </summary>
    [JsonPropertyName("isFirstRun")]
    public bool IsFirstRun { get; set; } = true;

    // ── Task timeouts ─────────────────────────────────────────────────────────

    /// <summary>Deadline in seconds for quick tasks: explain, fix, doc, inline edit, agent plan. Default: 120.</summary>
    [JsonPropertyName("quickTimeoutSeconds")]
    public int QuickTimeoutSeconds { get; set; } = 120;

    /// <summary>Deadline in seconds per agent turn for tool-enabled chat and orchestrator steps. Default: 300.</summary>
    [JsonPropertyName("normalTimeoutSeconds")]
    public int NormalTimeoutSeconds { get; set; } = 300;

    /// <summary>Deadline in seconds for extended reasoning tasks (reserved for future use). Default: 600.</summary>
    [JsonPropertyName("deepTimeoutSeconds")]
    public int DeepTimeoutSeconds { get; set; } = 600;

    // ── Autonomous Agent Mode ─────────────────────────────────────────────────

    /// <summary>
    /// When <c>true</c>, every agentic request goes through the
    /// <see cref="Inferpal.Services.Agent.AgentOrchestrator"/> which generates an explicit JSON plan
    /// before calling any tools, then runs a Plan → Act → Observe loop with live step tracking.
    /// Disable for simpler / faster queries that don't need structured planning.
    /// </summary>
    [JsonPropertyName("agentModeEnabled")]
    public bool AgentModeEnabled
    {
        get => _agentModeEnabled;
        set
        {
            if (_agentModeEnabled == value) return;
            _agentModeEnabled = value;
            AgentModeEnabledChanged?.Invoke(value);
        }
    }
    private bool _agentModeEnabled = false;

    /// <summary>
    /// Raised whenever <see cref="AgentModeEnabled"/> actually changes value (deserialization,
    /// the main-window Chat/Agent switch, or the Settings checkbox). Lets the two UI surfaces stay
    /// in sync live without polling. Not serialized. Handlers must marshal to their own UI context.
    /// </summary>
    public event Action<bool>? AgentModeEnabledChanged;

    /// <summary>
    /// Raised after the UI language changes at runtime (Settings save, once the culture override is
    /// applied). Lets already-open surfaces — notably the chat tool window — re-localize their bound
    /// labels live instead of only on next load. Not serialized; handlers must marshal to their own
    /// UI context.
    /// </summary>
    public event Action? LanguageChanged;

    /// <summary>Raises <see cref="LanguageChanged"/>. Call only after <c>Strings.ApplyLanguage</c>
    /// has updated the active culture, so handlers read the new strings.</summary>
    internal void NotifyLanguageChanged() => LanguageChanged?.Invoke();

    /// <summary>
    /// Maximum number of Plan → Act → Observe iterations the agent is allowed to run.
    /// <c>0</c> falls back to the default cap (<see cref="Services.Agent.AgentOrchestrator.DefaultMaxIterations"/>,
    /// 20) — there is no unlimited mode.
    /// </summary>
    [JsonPropertyName("agentMaxIterations")]
    public int AgentMaxIterations { get; set; } = 20;

    // ── VRAM / Model lifetime ─────────────────────────────────────────────────

    /// <summary>
    /// When <c>true</c>, every <c>/api/chat</c> and <c>/api/generate</c> request includes a
    /// <c>keep_alive</c> parameter equal to <see cref="ModelIdleTimeoutMinutes"/> minutes,
    /// instructing Ollama to automatically evict the model from VRAM after that idle period.
    /// </summary>
    [JsonPropertyName("modelAutoUnloadEnabled")]
    public bool ModelAutoUnloadEnabled { get; set; } = true;

    /// <summary>
    /// Minutes of inactivity after which Ollama unloads the model from VRAM (minimum 1).
    /// Only effective when <see cref="ModelAutoUnloadEnabled"/> is <c>true</c>.
    /// </summary>
    [JsonPropertyName("modelIdleTimeoutMinutes")]
    public int ModelIdleTimeoutMinutes { get; set; } = 10;

    // ── LSP Semantic Provider ─────────────────────────────────────────────────

    /// <summary>
    /// When <c>true</c>, installed language servers (<c>typescript-language-server</c>,
    /// <c>pylsp</c>/<c>pyright-langserver</c>, <c>gopls</c>, <c>rust-analyzer</c>) are
    /// used to extract semantic symbols from TypeScript, JavaScript, Python, Go, and Rust
    /// files during RAG indexing.  This replaces the sliding-window heuristic with precise
    /// function/class/method boundaries.  Requires the relevant server to be on PATH.
    /// </summary>
    [JsonPropertyName("lspEnabled")]
    public bool LspEnabled { get; set; } = false;

    public static InferpalConfig Load()
    {
        InferpalConfig cfg;
        if (!File.Exists(ConfigPath))
        {
            cfg = new InferpalConfig();
        }
        else
        {
            try
            {
                var json = File.ReadAllText(ConfigPath);
                cfg = JsonSerializer.Deserialize<InferpalConfig>(json) ?? new InferpalConfig();
            }
            catch { cfg = new InferpalConfig(); }
        }
        Strings.ApplyLanguage(cfg.Language);
        return cfg;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }
}
