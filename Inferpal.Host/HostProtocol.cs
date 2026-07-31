namespace Inferpal.Host;

// ── Wire DTOs of the Host ⇄ editor-adapter protocol ──────────────────────────
// Serialized by StreamJsonRpc's SystemTextJsonFormatter; every request carries a
// single named-parameter object (UseSingleObjectParameterDeserialization), which is
// what vscode-jsonrpc sends by default from the TypeScript side.

/// <summary>`initialize` — first request of a session; builds the whole service graph.</summary>
/// <param name="RootDir">Workspace root: file-tool confinement + `.inferpal/` project layers.</param>
/// <param name="Locale">Editor display language (e.g. <c>fr</c>, <c>zh-cn</c>); null keeps the OS culture.</param>
/// <param name="ClientName">Free-form adapter identity for diagnostics (e.g. <c>vscode/1.102</c>).</param>
internal sealed record InitializeParams(string RootDir, string? Locale = null, string? ClientName = null);

/// <summary>What the adapter learns about the backend at startup (gates UI features).</summary>
internal sealed record InitializeResult(
    string HostVersion,
    string Provider,
    string DefaultModel,
    bool   ModelManagement,
    bool   VramMonitoring,
    bool   Fim,
    bool   KeepAlive);

/// <summary>`chat/send` — one user turn. Tokens/steps stream back as notifications.</summary>
/// <param name="AgentMode">Overrides the configured agent-mode switch for this turn; null = config.</param>
internal sealed record ChatSendParams(string Prompt, string? Model = null, bool? AgentMode = null);

/// <summary>Final outcome of a turn. <paramref name="Text"/> holds the partial stream on cancel.</summary>
internal sealed record ChatSendResult(
    string  Text,
    bool    Cancelled,
    int     TokensUsed,
    int     PromptTokens,
    string? Error = null);

/// <summary>`chat/tool` notification — one executed tool call (uncapped output, like the VS bubble).</summary>
internal sealed record ToolNotice(string Name, string Input, string Output, bool HasErrors);

/// <summary>`fim/complete` — ghost-text request; cancellation aborts the LLM call via the RPC token.</summary>
internal sealed record FimParams(
    string  Prefix,
    string  Suffix,
    int     MaxTokens   = 128,
    double  Temperature = 0.2,
    string? Model       = null);

/// <summary>`textDocument/didOpen|didChange|didClose` + `editor/didChangeActiveDocument`.</summary>
internal sealed record DocumentParams(string Path, string? Text = null);

/// <summary>Reverse `editor/activeDocument` answer from the adapter.</summary>
internal sealed record ActiveDocumentDto(string? Path, string? Text);

/// <summary>Reverse `editor/replaceSelection` answer from the adapter.</summary>
internal sealed record EditResultDto(string? Path, bool ReplacedSelection);

/// <summary>`index/status` snapshot.</summary>
internal sealed record IndexStatusResult(bool IsIndexing, int ChunkCount, string RootDir);

/// <summary>`backend/status` answer — the adapter's connection badge. <paramref name="VramBadge"/>
/// is the compact "model · X.X GB" line (empty when unreachable, unsupported or nothing loaded).</summary>
internal sealed record BackendStatusResult(bool Connected, string VramBadge);

/// <summary>`command/list` entry — one slash command for the adapter's autocomplete popup
/// (built-ins with their localized hints, then user templates).</summary>
internal sealed record SlashCommandInfoDto(string Command, string Hint);

// ── Typed @-mentions (categories served by the Core's MentionController) ─────

/// <summary>`mention/categories` entry — one @mention category with its localized
/// description. Query-based categories (@file/@code/@folder) drill into a sub-search;
/// instant ones attach their context directly.</summary>
internal sealed record MentionCategoryDto(string Token, string Description, bool QueryBased);

/// <summary>`mention/search` — sub-search of a query-based category (<c>file</c> | <c>folder</c>).</summary>
internal sealed record MentionSearchParams(string Category, string Query);

/// <summary>One `mention/search` hit: display label + relative detail + the value to
/// resolve or insert (full path for files/folders).</summary>
internal sealed record MentionItemDto(string Label, string Detail, string Value);

/// <summary>`mention/resolve` — materializes an instant or selected mention host-side.
/// <paramref name="Category"/> ∈ <c>tree</c> | <c>diff</c> | <c>folder</c> (Value = full path) |
/// <c>code</c> (Value = semantic query).</summary>
internal sealed record MentionResolveParams(string Category, string? Value = null);

/// <summary>`mention/resolve` answer: the chip label + attached content (null = nothing).</summary>
internal sealed record MentionResolveResult(string? Name, string? Content);

/// <summary>`command/slash` — a chat input starting with <c>/</c>. The host executes the
/// commands it can serve headlessly; <c>Handled = false</c> tells the adapter to send the
/// text as a normal chat prompt instead. <paramref name="PromptHistory"/> is the adapter's
/// prompt-box history (most-recent-last), consumed by <c>/phistory</c>.</summary>
internal sealed record SlashCommandParams(string Text, List<string>? PromptHistory = null);

/// <summary>One editor-side effect a handled slash command asks the adapter to apply.
/// <paramref name="Kind"/> ∈ <c>setPrompt</c> (Value = text to put in the prompt box) |
/// <c>sendAsPrompt</c> (Value = expanded template to send as a normal chat turn) |
/// <c>attachChip</c> (Name = chip label, Value = content to attach to the next turn) |
/// <c>copyToClipboard</c> (Value) | <c>clearTranscript</c> | <c>stateChange</c>
/// (Name = key e.g. <c>model</c>, Value) | <c>openFile</c> (Value = absolute path) |
/// <c>exportRequest</c>. Unknown kinds must be ignored (forward compatibility).</summary>
internal sealed record SlashEffectDto(string Kind, string? Value = null, string? Name = null);

/// <summary>`command/slash` answer: <paramref name="Markdown"/> is the bubble to render
/// when <paramref name="Handled"/> is true; <paramref name="Effects"/> are the editor-side
/// side effects to apply (null/empty = none).</summary>
internal sealed record SlashCommandResult(
    bool Handled, string? Markdown = null, List<SlashEffectDto>? Effects = null);

/// <summary>`config/update` — full config JSON, as previously returned by `config/get`.</summary>
internal sealed record ConfigUpdateParams(string Json);

/// <summary>`codeAction/run` — headless in-place code action (<paramref name="Kind"/> =
/// <c>fix</c> | <c>refactor</c> | <c>doc</c>) over the adapter's document text and selection
/// offsets. The host only runs the model step; applying (and previewing) stays editor-side.</summary>
internal sealed record CodeActionParams(
    string  Kind,
    string  Text,
    int     SelStart,
    int     SelEnd,
    string? Model = null);

/// <summary>One accepted-or-rejected-independently hunk of a code action rewrite, as a
/// character-offset edit against the submitted text (mirror of the Core's <c>DiffEdit</c>).</summary>
internal sealed record CodeActionEditDto(int Index, int Start, int End, string NewText);

/// <summary>`codeAction/run` answer. <paramref name="Outcome"/> is <c>edited</c> (apply or
/// preview <paramref name="Edits"/>), <c>noChange</c> (model judged the code already good) or
/// <c>failed</c>. <paramref name="NewText"/> is the full rewritten document when edited;
/// <paramref name="FailureDetail"/> is the underlying error message when failed.</summary>
internal sealed record CodeActionResultDto(
    string                  Outcome,
    List<CodeActionEditDto> Edits,
    string?                 NewText = null,
    string?                 FailureDetail = null);

// ── Context X-Ray panel (interactive /xray V2) ───────────────────────────────

/// <summary>One prompt layer of the X-Ray panel (wire mirror of the Core's <c>XRaySectionModel</c>).</summary>
internal sealed record XRaySectionDto(
    string Id, string Label, int Tokens, double Percent, string Content, bool Enabled, bool CanToggle);

/// <summary>`xray/panel` (and `xray/toggle`) answer: the full panel model, ready to render.</summary>
internal sealed record XRayPanelDto(
    List<XRaySectionDto> Sections,
    int    TotalTokens,
    int    HistoryTokens,
    int    ContextWindow,
    double FillPercent,
    bool   OverheadWarning,
    string RawPrompt);

/// <summary>`xray/toggle` — switches one section on/off for the next turns of this session.</summary>
internal sealed record XRayToggleParams(string Id, bool Enabled);

// ── Sessions (persisted in %AppData%/Inferpal/sessions/, shared with the VS extension) ──

/// <summary>One display message of a saved session (wire mirror of <c>SavedMessage</c>).</summary>
internal sealed record SavedMessageDto(string Role, string Content, string? ToolName = null, string? Timestamp = null);

/// <summary>`session/save` — persists the adapter's transcript under <paramref name="Name"/>.</summary>
internal sealed record SessionSaveParams(string Name, List<SavedMessageDto> Messages);

/// <summary>`session/load` / `session/delete` argument.</summary>
internal sealed record SessionRefParams(string Name);

/// <summary>`session/list` entry. <paramref name="Parent"/>/<paramref name="ForkTurn"/> are set on
/// branches (<c>/branch</c>) so the adapter can show the family in its session picker.</summary>
internal sealed record SessionSummaryDto(string Name, DateTime SavedAt, int MessageCount, string Preview,
                                         string? Parent = null, int? ForkTurn = null);

/// <summary>`session/load` answer: the transcript to re-render (host history already rebuilt).</summary>
internal sealed record SessionLoadResult(string Name, List<SavedMessageDto> Messages);

/// <summary>`session/branch` — fork the adapter's transcript at 1-based <paramref name="Turn"/>.</summary>
internal sealed record SessionBranchParams(int Turn, List<SavedMessageDto> Messages);

/// <summary>`session/branch` answer: the new branch (host history already rebuilt from it) plus the
/// localized confirmation bubble — the adapter has no access to the shared .resx.</summary>
internal sealed record SessionBranchResult(
    string Name, string Parent, int ForkTurn, List<SavedMessageDto> Messages, string Message);

/// <summary>`session/title` — text to summarise; empty = the session's first user message.</summary>
internal sealed record SessionTitleParams(string? Text = null);

/// <summary>`session/title` answer: the bare title plus the timestamped save file name.</summary>
internal sealed record SessionTitleResult(string Title, string FileName);

// ── Settings schema (`settings/schema`) ──────────────────────────────────────

/// <summary>One choice of a select field. Texts are product names — never localized.</summary>
internal sealed record SettingsOptionDto(string Value, string Text);

/// <summary>An editable setting: <paramref name="Label"/>/<paramref name="Hint"/> are resource
/// names the adapter resolves against `settings/strings`.</summary>
internal sealed record SettingsFieldDto(
    string Key, string Kind, string Label, string? Hint, string? Unit, string? Gate, string? Button,
    List<SettingsOptionDto>? Options);

/// <summary>A titled group of fields, with an optional reveal toggle.</summary>
internal sealed record SettingsSectionDto(
    string Title, List<SettingsFieldDto> Fields,
    string? ToggleGate, string? ToggleLabel, string? ToggleHint);

/// <summary>One tab of the settings window.</summary>
internal sealed record SettingsTabDto(string Key, string Title, List<SettingsSectionDto> Sections);

/// <summary>`settings/schema` answer: the tabs plus the fields rendered outside them.</summary>
internal sealed record SettingsSchemaDto(List<SettingsTabDto> Tabs, List<SettingsFieldDto> HeaderFields);
