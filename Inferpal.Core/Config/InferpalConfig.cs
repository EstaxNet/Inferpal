using System.IO;
using System.Linq;
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

    /// <summary>
    /// Test seam: redirects <see cref="Load"/>/<see cref="Save"/> to another file. Set once by the
    /// test assembly's module initializer so no test can ever clobber the developer's real
    /// %AppData% config (handlers legitimately call <see cref="Save"/>). Null = production path.
    /// </summary>
    internal static string? OverridePathForTests;

    private static string EffectiveConfigPath => OverridePathForTests ?? ConfigPath;

    /// <summary>Whether a settings file exists — an editor, or the setup, wrote one. Absent, <see cref="Load"/> returns
    /// the factory settings.</summary>
    internal static bool SettingsFileExists => File.Exists(EffectiveConfigPath);

    /// <summary>Where the settings live, as the setup names it.</summary>
    internal static string SettingsFilePath => EffectiveConfigPath;

    /// <summary>Test seam of ONE instance: where its <see cref="Save"/> writes. A test that needs a file of its own
    /// sets this — never <see cref="OverridePathForTests"/>, which belongs to the whole suite.</summary>
    internal string? SavePathForTests { get; init; }

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

    /// <summary>How much space the chat leaves around its turns: <c>comfortable</c> (the default) or <c>compact</c>,
    /// which fits more of the conversation in a narrow panel. Anything else reads as comfortable.</summary>
    [JsonPropertyName("chatDensity")]
    public string ChatDensity { get; set; } = "comfortable";

    /// <summary>The chat is drawn compact (<see cref="ChatDensity"/>).</summary>
    [JsonIgnore]
    public bool IsCompactChat => string.Equals(ChatDensity, "compact", StringComparison.OrdinalIgnoreCase);

    /// <summary>When <c>true</c>, <c>write_file</c> and <c>run_command</c> skip the approval dialog.</summary>
    [JsonPropertyName("securityAlertsDisabled")]
    public bool SecurityAlertsDisabled { get; set; } = false;

    /// <summary>
    /// Inline diff preview for in-place code actions (/fix /refactor /doc): the rewrite is shown
    /// as per-hunk ✓/✗ overlays in the editor instead of being applied immediately. Falls back to
    /// the direct apply when no in-process renderer picks the request up. Comfort feature only —
    /// orthogonal to tool approval, which it never replaces nor bypasses.
    /// </summary>
    [JsonPropertyName("inlineDiffPreviewEnabled")]
    public bool InlineDiffPreviewEnabled { get; set; } = true;

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

    /// <summary>
    /// When <c>true</c>, chat and agent requests carry the sampling settings the model's vendor recommends for a family
    /// Inferpal knows (<c>ModelProfiles</c>, documented in docs/models.md) — only the values the vendor publishes.
    /// <c>false</c> sends none, and the server's own per-model settings apply.
    /// </summary>
    /// <remarks>⚠ A server applies its generic defaults to a model it has no preset for — LM Studio's include a
    /// repeat penalty that GLM's vendor says to disable — and the user rarely knows which models have a preset.</remarks>
    [JsonPropertyName("useRecommendedSampling")]
    public bool UseRecommendedSampling { get; set; } = true;

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
    [CollectionSetting(CollectionShape.Lines)]
    [JsonPropertyName("pinnedContextFiles")]
    public string PinnedContextFiles { get; set; } = string.Empty;

    /// <summary>
    /// The families of instruction files the repository gives coding agents that are read and sent with questions —
    /// <c>agents, copilot, claude, cursor, cline, roo, continue</c>, comma-separated; all by default, empty = none.
    /// </summary>
    [JsonPropertyName("repoInstructionFamilies")]
    public string RepoInstructionFamilies { get; set; } = "agents, copilot, claude, cursor, cline, roo, continue";

    /// <summary>
    /// The skills' automatic mode: their names and descriptions in every question's prompt, the model loading the one a
    /// request needs (<c>read_skill_file</c>). Off by default: <c>/skill</c> is then the only way to use one.
    /// </summary>
    [JsonPropertyName("skillsAutoMode")]
    public bool SkillsAutoMode { get; set; } = false;

    /// <summary>
    /// User-defined slash command templates, one per line in the format <c>/name=text</c>.
    /// Use <c>{args}</c> as a placeholder for extra words typed after the command name.
    /// </summary>
    [CollectionSetting(CollectionShape.Lines)]
    [JsonPropertyName("promptTemplates")]
    public string PromptTemplates { get; set; } = string.Empty;

    /// <summary>
    /// User-defined shell tools exposed to the agent, one per line: <c>name=powershell_command</c>.
    /// The tool name must be snake_case. The command runs in PowerShell with an optional
    /// <c>args</c> parameter appended when the model provides extra arguments.
    /// </summary>
    [CollectionSetting(CollectionShape.Lines)]
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
    [CollectionSetting(CollectionShape.Lines)]
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

    /// <summary>
    /// Model for auxiliary background tasks: session titles, <c>/commit</c> message proposals and
    /// history-compaction summaries. Empty string = use <see cref="DefaultModel"/>. A small fast
    /// model (e.g. a 3-7B) is ideal here — these tasks need seconds-level latency, not deep
    /// reasoning, and <c>keep_alive</c> keeps it warm between calls. Resolution is centralized in
    /// <see cref="Services.Inference.ModelRouter"/>.
    /// </summary>
    [JsonPropertyName("utilityModel")]
    public string UtilityModel { get; set; } = string.Empty;

    /// <summary>
    /// Model Router "auto" mode (V2). When <c>true</c> and no explicit <see cref="UtilityModel"/>
    /// is set, utility tasks are routed to the model <c>/bench</c> recommended for the utility
    /// role — but only when that model is already loaded (warm): swapping a model in VRAM costs
    /// more than a title or commit message saves, so a cold candidate falls back to the chat
    /// model. Resolution lives in <see cref="Services.Inference.ModelRouter.ResolveUtilityAsync"/>.
    /// </summary>
    [JsonPropertyName("modelRouterAuto")]
    public bool ModelRouterAuto { get; set; } = false;

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
    /// Model used to generate embedding vectors for RAG indexing. Empty string falls back to
    /// <see cref="Services.Inference.EmbeddingModels.Default"/>; read through <c>EmbeddingModels.Resolve</c>, never here.
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
    [CollectionSetting(CollectionShape.JsonObjectByName)]
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
    [CollectionSetting(CollectionShape.JsonArrayById)]
    [JsonPropertyName("docSitesJson")]
    public string DocSitesJson { get; set; } = string.Empty;

    /// <summary>
    /// <c>true</c> until the first successful connection + model discovery runs.
    /// Reset to <c>false</c> permanently after first-run setup completes (even if no models found).
    /// </summary>
    [JsonPropertyName("isFirstRun")]
    public bool IsFirstRun { get; set; } = true;

    // ── Task timeouts ─────────────────────────────────────────────────────────

    /// <summary>Deadline in seconds for quick tasks: explain, review, fix, doc, inline edit, agent plan. Default: 120.</summary>
    [JsonPropertyName("quickTimeoutSeconds")]
    public int QuickTimeoutSeconds { get; set; } = 120;

    /// <summary>Deadline in seconds per turn for a chat question, with or without tools, and orchestrator steps
    /// (<see cref="Services.Agent.ChatTurnPolicy.TurnComplexity"/>). Default: 300.</summary>
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
    /// Raised after every <see cref="Save"/>. The chat window measures its context window again on it: the window in
    /// use is the one the last turn measured, so after a new window or a new model X-Ray and the gauge kept the old
    /// number until the next question. Not serialized; handlers must marshal to their own UI context.
    /// </summary>
    public event Action? Saved;

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

    // Cached parse of the config file, invalidated by its last-write stamp. Load() sits on hot
    // paths — the ghost-text controller calls it on every keystroke — and re-reading + re-parsing
    // JSON from disk on the UI thread for each typed character is a stutter waiting for a slow
    // disk or an antivirus. Same stamp-based approach as the permission-policy cache.
    private static readonly object          _loadLock = new();
    private static InferpalConfig?          _cached;
    private static string?                  _cachedPath;
    private static DateTime                 _cachedStamp;

    /// <summary>Reads the configuration, reusing the last parse while the file is unchanged.</summary>
    public static InferpalConfig Load()
    {
        var path  = EffectiveConfigPath;
        var stamp = StampOf(path);

        lock (_loadLock)
        {
            if (_cached is not null && _cachedPath == path && _cachedStamp == stamp)
                return _cached;
        }

        var fresh = LoadUncached();

        lock (_loadLock)
        {
            _cached      = fresh;
            _cachedPath  = path;
            _cachedStamp = stamp;
        }
        return fresh;
    }

    private static DateTime StampOf(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default; }
        catch (Exception ex) { Services.Diagnostics.Swallow("InferpalConfig.Stamp", ex); return default; }
    }

    private static InferpalConfig LoadUncached()
    {
        var path = EffectiveConfigPath;
        InferpalConfig cfg;

        if (!File.Exists(path))
        {
            // First install: the ordinary case. Nothing is traced - a channel that speaks on the
            // ordinary path stops being read.
            cfg = new InferpalConfig();
        }
        else if (TryRead(path, out var parsed, out var error, out var repaired))
        {
            cfg = parsed!;
            if (repaired.Count > 0)
                Services.Diagnostics.Record("InferpalConfig.Load",
                    $"{path} holds null for {string.Join(", ", repaired)} — factory defaults are used for "
                    + "these settings, and the next save writes them back.");
        }
        else
        {
            // The fallback to factory defaults stays — the product has to start — but it is never
            // MUTE: a user whose backends, per-role models, MCP servers and permission rules have
            // "disappeared" otherwise has nowhere to look, and nothing tells that apart from a
            // first install.
            Services.Diagnostics.Record("InferpalConfig.Load",
                $"{path} exists but could not be read ({error!.GetType().Name}: {error.Message}) — "
                + "factory defaults are used for this session, and the file will be set aside at the "
                + "next save rather than overwritten.");
            cfg = new InferpalConfig();
        }

        Strings.ApplyLanguage(cfg.Language);
        cfg._baseline = Snapshot(cfg);
        return cfg;
    }

    /// <summary>
    /// Reads and parses the file. <c>false</c> when it exists but cannot be turned into a
    /// configuration - a write torn by a hard shutdown, a disk error, one comma too many after a
    /// hand edit, a file held by another process, or <c>null</c> content.
    /// </summary>
    private static bool TryRead(string path, out InferpalConfig? cfg, out Exception? error, out List<string> repaired)
    {
        cfg = null;
        error = null;
        repaired = [];
        try
        {
            cfg = JsonSerializer.Deserialize<InferpalConfig>(File.ReadAllText(path));
            if (cfg is not null)
            {
                repaired = RepairNullText(cfg);
                CanonicalizeChoices(cfg);
                return true;
            }
            error = new JsonException("the file is valid JSON but holds no object");
            return false;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    // The non-nullable text settings and their JSON keys. A hand-edited file can hold `null` there, and
    // System.Text.Json writes it as is into the property: the failure then surfaced far from its cause
    // (every backend call trims BaseUrl).
    private static readonly (System.Reflection.PropertyInfo Prop, string Key)[] NonNullableText = BuildNonNullableText();

    private static (System.Reflection.PropertyInfo, string)[] BuildNonNullableText()
    {
        var nullability = new System.Reflection.NullabilityInfoContext();
        return typeof(InferpalConfig)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.CanRead && p.CanWrite
                        && nullability.Create(p).WriteState == System.Reflection.NullabilityState.NotNull)
            .Select(p => (p, p.GetCustomAttributes(typeof(JsonPropertyNameAttribute), false)
                               .Cast<JsonPropertyNameAttribute>().FirstOrDefault()?.Name ?? p.Name))
            .ToArray();
    }

    /// <summary>Puts each null non-nullable text setting back to its factory value; returns their keys.</summary>
    private static List<string> RepairNullText(InferpalConfig cfg)
    {
        var repaired = new List<string>();
        InferpalConfig? defaults = null;
        foreach (var (prop, key) in NonNullableText)
        {
            if (prop.GetValue(cfg) is not null) continue;
            defaults ??= new InferpalConfig();
            prop.SetValue(cfg, prop.GetValue(defaults));
            repaired.Add(key);
        }
        return repaired;
    }

    /// <summary>
    /// Brings the choice settings (<c>provider</c>, <c>language</c>, <c>inlineCompletionMode</c>) written in an
    /// equivalent form — case, documented alias, region of an offered language — back to the code of the option they name.
    /// </summary>
    /// <remarks>
    /// Both settings windows select the option whose code is EQUAL to the value read: <c>"LMStudio"</c>, which the
    /// factory serves as LM Studio, showed there as "Ollama", and the first Save wrote <c>ollama</c>. A value no
    /// option names stays as is: it is not for the reader to guess what it meant.
    /// </remarks>
    internal static void CanonicalizeChoices(InferpalConfig cfg)
    {
        cfg.Provider = Services.Inference.InferenceProviderFactory.Canonical(cfg.Provider);
        cfg.InlineCompletionMode = OptionCode(Services.Presentation.SettingsSchema.FimModes, cfg.InlineCompletionMode)
                                   ?? cfg.InlineCompletionMode;

        // "fr-FR" names "fr": trailing subtags are dropped until an offered language matches.
        var languages = Services.Presentation.SettingsSchema.Languages;
        var code = cfg.Language.Trim();
        while (code.Length > 0)
        {
            if (OptionCode(languages, code) is { } match)
            {
                cfg.Language = match;
                break;
            }
            var dash = code.LastIndexOf('-');
            code = dash > 0 ? code[..dash] : string.Empty;
        }
    }

    private static string? OptionCode(IReadOnlyList<Services.Presentation.SettingOption> options, string value) =>
        options.FirstOrDefault(o => o.Value.Length > 0
                                    && string.Equals(o.Value, value.Trim(), StringComparison.OrdinalIgnoreCase))?.Value;

    // What this instance held when it was read from disk, or at its last save. Null for an instance
    // built in code, which is then written whole.
    private System.Text.Json.Nodes.JsonObject? _baseline;

    public void Save()
    {
        var path = SavePathForTests ?? EffectiveConfigPath;
        PreserveUnreadableFile(path);

        // Visual Studio and VS Code each keep their own copy of this file in memory. Written whole, a
        // save reverted every setting the other editor had changed since this copy was read — a
        // model, a backend, a deny rule. Only what THIS copy changed is laid over the file.
        var mine    = Snapshot(this);
        var toWrite = _baseline is null                       ? mine
                    : TryReadSnapshot(path, out var onDisk)  ? MergeChanges(onDisk, mine, _baseline)
                    // No readable file (a fresh install): the factory values stand for it, so a value set for
                    // the session only (UseDefaultModelForSession) stays out of it too.
                    : MergeChanges(Snapshot(new InferpalConfig()), mine, _baseline);

        // Atomic: a torn write here leaves the user without a usable configuration, and this
        // runs on every /model, /hardware, /docs and settings save.
        Services.Persistence.AtomicFile.WriteAllText(path,
            toWrite.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        // The in-memory values are left alone: the file's values are not reloaded here. ⚠ A backend
        // switched in Settings IS laid over the live instance (ApplyChangesFrom) while Visual Studio's
        // client stays the previous backend's until restart — InferenceProviderFactory.PendingSwitch
        // is what the connection messages read to say so. The next save compares against what this
        // copy holds now, so what it did not change keeps coming from the file.
        _baseline = mine;

        // Drop the cached parse: a save within the same file-time tick would otherwise keep
        // serving the previous values to Load().
        lock (_loadLock) { _cached = null; _cachedPath = null; _cachedStamp = default; }

        Saved?.Invoke();
    }

    private static System.Text.Json.Nodes.JsonObject Snapshot(InferpalConfig cfg) =>
        JsonSerializer.SerializeToNode(cfg)!.AsObject();

    // The file in the shape this class writes it (same keys, defaults filled), so it compares key by
    // key with a snapshot — PLUS every key this version does not know. ⚠ Read only through the typed
    // class, a setting written by a newer version (the other editor updated first) vanished at the next
    // save. False when absent or unreadable: this copy is then written whole.
    private static bool TryReadSnapshot(string path, out System.Text.Json.Nodes.JsonObject snapshot)
    {
        snapshot = null!;
        if (!File.Exists(path) || !TryRead(path, out var cfg, out _, out _)) return false;
        snapshot = Snapshot(cfg!);
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) is System.Text.Json.Nodes.JsonObject raw)
                foreach (var (key, value) in raw)
                    if (!snapshot.ContainsKey(key))
                        snapshot[key] = value?.DeepClone();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Inferpal.Services.Diagnostics.Swallow("InferpalConfig.UnknownKeys", ex);
        }
        return true;
    }

    /// <summary>
    /// <paramref name="onDisk"/>, with every setting <paramref name="mine"/> changed since
    /// <paramref name="baseline"/> laid over it. A setting both sides changed goes to
    /// <paramref name="mine"/> — element by element for a collection (<see cref="CollectionSettingAttribute"/>).
    /// </summary>
    internal static System.Text.Json.Nodes.JsonObject MergeChanges(
        System.Text.Json.Nodes.JsonObject onDisk,
        System.Text.Json.Nodes.JsonObject mine,
        System.Text.Json.Nodes.JsonObject baseline)
    {
        var merged = (System.Text.Json.Nodes.JsonObject)onDisk.DeepClone();
        foreach (var (key, value) in mine)
        {
            if (System.Text.Json.Nodes.JsonNode.DeepEquals(value, baseline[key])) continue;
            merged[key] = CollectionSettings.TryGetValue(key, out var shape)
                          && !System.Text.Json.Nodes.JsonNode.DeepEquals(onDisk[key], baseline[key])
                          && CollectionMerge.Merge(shape, Text(baseline[key]), Text(onDisk[key]), Text(value)) is { } both
                ? System.Text.Json.Nodes.JsonValue.Create(both)
                : value?.DeepClone();
        }
        return merged;
    }

    private static string Text(System.Text.Json.Nodes.JsonNode? node) =>
        node is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;

    /// <summary>The JSON key of every setting that holds a collection, with its shape — read from the properties.</summary>
    internal static readonly IReadOnlyDictionary<string, CollectionShape> CollectionSettings =
        typeof(InferpalConfig).GetProperties()
            .Select(p => (Json: p.GetCustomAttributes(typeof(JsonPropertyNameAttribute), false).OfType<JsonPropertyNameAttribute>().FirstOrDefault(),
                          Shape: p.GetCustomAttributes(typeof(CollectionSettingAttribute), false).OfType<CollectionSettingAttribute>().FirstOrDefault()))
            .Where(x => x.Json is not null && x.Shape is not null)
            .ToDictionary(x => x.Json!.Name, x => x.Shape!.Shape, StringComparer.Ordinal);

    /// <summary>What this instance holds now, in the shape it is written.</summary>
    internal System.Text.Json.Nodes.JsonObject SnapshotNow() => Snapshot(this);

    /// <summary>
    /// Before an AUTOMATIC decision fills a setting nobody set (a first run, a model adopted, a VRAM budget detected):
    /// when this copy still holds the setting's factory value and the FILE holds another, this copy takes the file's.
    /// </summary>
    /// <param name="property">The C# property name (<c>nameof</c>).</param>
    /// <returns><c>true</c> when the file's value was taken — the decision is then the other window's, already made.</returns>
    /// <remarks>
    /// ⚠ Every window of both editors keeps its own copy, read once. "Still unset?" asked of this copy alone said yes
    /// after the other window had set it — a budget the user typed, a model they picked — and the automatic value was
    /// written over it.
    /// </remarks>
    /// <summary>The permission rules that decide NOW: see <see cref="Shared{T}"/>.</summary>
    internal string SharedPermissionRules => Shared(nameof(PermissionRules), PermissionRules) ?? string.Empty;

    /// <summary>Whether security alerts are off NOW: see <see cref="Shared{T}"/>.</summary>
    internal bool SharedSecurityAlertsDisabled => Shared(nameof(SecurityAlertsDisabled), SecurityAlertsDisabled);

    private sealed record FileSeen(string Path, DateTime Stamp, System.Text.Json.Nodes.JsonObject Snapshot);
    private readonly object _sharedLock = new();
    private FileSeen? _fileSeen;

    /// <summary>
    /// A setting that DECIDES (an approval), as it stands now: this copy's own value when it changed it since it was
    /// read, else the file's — which the other editor, or another window, may have changed since.
    /// </summary>
    /// <remarks>
    /// ⚠ Each window of both editors reads the file once and keeps its copy: an <c>allow</c> rule removed, or security
    /// alerts turned back on, in one window went on approving unasked in the others until they restarted. A copy built
    /// in code (no baseline) is its own truth. The file is read again only when its stamp moves.
    /// </remarks>
    private T Shared<T>(string property, T mine)
    {
        lock (_sharedLock)
        {
            if (_baseline is null) return mine;
            var key = JsonNameOf(property);
            if (!System.Text.Json.Nodes.JsonNode.DeepEquals(Snapshot(this)[key], _baseline[key])) return mine;   // changed here

            var path  = SavePathForTests ?? EffectiveConfigPath;
            var stamp = StampOf(path);
            if (_fileSeen is not { } seen || seen.Path != path || seen.Stamp != stamp)
            {
                if (!TryReadSnapshot(path, out var onDisk)) return mine;
                _fileSeen = seen = new FileSeen(path, stamp, onDisk);
            }
            try { return seen.Snapshot[key] is { } value ? value.Deserialize<T>()! : mine; }
            catch (JsonException) { return mine; }
        }
    }

    private static string JsonNameOf(string property) =>
        (typeof(InferpalConfig).GetProperty(property)
            ?? throw new ArgumentException($"No setting named {property}.", nameof(property)))
        .GetCustomAttributes(typeof(JsonPropertyNameAttribute), false).OfType<JsonPropertyNameAttribute>().Single().Name;

    internal bool FillFromFile(string property)
    {
        var p = typeof(InferpalConfig).GetProperty(property)
                ?? throw new ArgumentException($"No setting named {property}.", nameof(property));
        var key = JsonNameOf(property);

        var factory = Snapshot(new InferpalConfig())[key];
        if (!System.Text.Json.Nodes.JsonNode.DeepEquals(Snapshot(this)[key], factory)) return false;   // set here: it stands
        if (!TryReadSnapshot(SavePathForTests ?? EffectiveConfigPath, out var onDisk)
            || System.Text.Json.Nodes.JsonNode.DeepEquals(onDisk[key], factory))
            return false;

        p.SetValue(this, onDisk[key].Deserialize(p.PropertyType));
        // Taken from the file, so not a change of this copy: a later save leaves it to the file.
        if (_baseline is not null) _baseline[key] = onDisk[key]?.DeepClone();
        return true;
    }

    /// <summary>
    /// Sets <see cref="DefaultModel"/> for this instance's life without it ever being saved: a later
    /// <see cref="Save"/> — of any other setting — leaves the file's model alone.
    /// </summary>
    /// <remarks>⚠ <see cref="Save"/> writes what this copy changed since it was read, and a model set in
    /// memory is a change: a pin, <c>/hardware</c>, <c>/docs</c>, the Agent switch would each have written
    /// VS Code's window model into the file Visual Studio reads at its next start. Recorded in the
    /// baseline, it is not a change; a model the user then picks is.</remarks>
    internal void UseDefaultModelForSession(string model)
    {
        _baseline ??= Snapshot(this);
        DefaultModel = model;
        var key = typeof(InferpalConfig).GetProperty(nameof(DefaultModel))!
            .GetCustomAttributes(typeof(JsonPropertyNameAttribute), false)
            .Cast<JsonPropertyNameAttribute>().First().Name;
        _baseline[key] = Snapshot(this)[key]?.DeepClone();
    }

    /// <summary>
    /// Lays over this live instance every setting <paramref name="edited"/> changed since
    /// <paramref name="baseline"/>; everything else keeps its current value.
    /// </summary>
    /// <remarks>
    /// An editor that built its values from <paramref name="baseline"/> must not write back what it only
    /// displayed: another part of the product may have changed that setting since — a model picked from
    /// the chat, a pinned file. <see cref="Save"/> applies the same rule against the file.
    /// </remarks>
    internal void ApplyChangesFrom(InferpalConfig edited, System.Text.Json.Nodes.JsonObject baseline)
    {
        var merged = JsonSerializer.Deserialize<InferpalConfig>(
            MergeChanges(Snapshot(this), Snapshot(edited), baseline))!;

        // Only what differs is set: a setter can notify (AgentModeEnabledChanged), and re-setting an
        // unchanged value would announce a change nobody made.
        foreach (var prop in typeof(InferpalConfig).GetProperties(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            if (prop.CanRead && prop.CanWrite
                && JsonSerializer.Serialize(prop.GetValue(this)) != JsonSerializer.Serialize(prop.GetValue(merged)))
                prop.SetValue(this, prop.GetValue(merged));
    }

    /// <summary>
    /// Copies an unreadable <c>config.json</c> aside before overwriting it.
    /// </summary>
    /// <remarks>
    /// Without this, an unreadable file is not merely <i>ignored</i>, it is <b>destroyed</b>: the
    /// load returns factory defaults, and the first <c>Save()</c> along — a setting, <c>/model</c>,
    /// <c>/hardware</c>, <c>/docs</c> — writes them over the original bytes.
    ///
    /// The state is judged <b>at the moment it matters</b>, by re-reading, rather than through a
    /// flag set at load time: a flag would be stale as soon as the user repairs the file by hand,
    /// and it would not cover a <c>Save()</c> from an instance built elsewhere. The cost is one
    /// re-read per save - the hot path is <see cref="Load"/>, not this one.
    /// </remarks>
    private static void PreserveUnreadableFile(string path)
    {
        try
        {
            if (!File.Exists(path) || TryRead(path, out _, out _, out _)) return;

            var aside = Services.Persistence.AtomicFile.PreserveAside(path);
            if (aside is not null)
                Services.Diagnostics.Record("InferpalConfig.Save",
                    $"{path} was unreadable: its bytes are kept in {aside} before being overwritten.");
        }
        catch (Exception ex)
        {
            // Best-effort: NEVER prevent the save. Failing to keep a copy is less serious than
            // leaving the user unable to write their settings.
            Services.Diagnostics.Swallow("InferpalConfig.PreserveUnreadable", ex);
        }
    }
}
