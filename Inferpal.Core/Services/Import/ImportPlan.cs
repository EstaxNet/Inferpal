using Inferpal.Services.Mcp;

namespace Inferpal.Services.Import;

/// <summary>The tool a configuration is imported from.</summary>
internal enum ImportSource { Continue, Roo }

/// <summary>What an imported item becomes in Inferpal — the line of the preview it is shown on.</summary>
internal enum ImportItemKind
{
    Backend, ChatModel, CompletionModel, EditModel, UtilityModel, EmbeddingModel, ContextWindow,
    Instructions, Command, McpServer, DocSite, PermissionRule, Language, Indexing,
}

/// <summary>
/// What applying an item does. Typed, so the applier never re-reads a label: the preview is drawn from these values and
/// the same values are written.
/// </summary>
internal abstract record ImportEffect;

/// <param name="Provider">An Inferpal provider code (<c>ollama</c>, <c>lmstudio</c>, <c>openai-compatible</c>).</param>
/// <param name="ApiKey"><c>null</c> = the key in place is kept.</param>
internal sealed record SetBackend(string Provider, string BaseUrl, string? ApiKey) : ImportEffect;

/// <param name="Role">Which model setting: <see cref="ImportItemKind.ChatModel"/>, <c>CompletionModel</c>,
/// <c>EditModel</c>, <c>UtilityModel</c> or <c>EmbeddingModel</c>.</param>
internal sealed record SetModel(ImportItemKind Role, string Model) : ImportEffect;

internal sealed record SetContextWindow(int Tokens) : ImportEffect;

/// <summary>A section added at the end of the custom system prompt, under <paramref name="Title"/>.</summary>
internal sealed record AddInstructions(string Title, string Text) : ImportEffect;

/// <summary>A command file in the machine's prompts folder: <c>/<paramref name="Name"/></c>.</summary>
internal sealed record WriteCommand(string Name, string? Description, string Text) : ImportEffect;

internal sealed record AddMcpServer(McpServerConfig Server) : ImportEffect;

internal sealed record AddDocSite(string Title, string StartUrl) : ImportEffect;

/// <summary>One <c>allow|deny tool regex</c> line of the permission rules.</summary>
internal sealed record AddPermissionRule(string Rule) : ImportEffect;

internal sealed record SetLanguage(string Code) : ImportEffect;

internal sealed record SetIndexing(bool Enabled) : ImportEffect;

/// <summary>Something said under an item: what it keeps, and what it does not.</summary>
internal enum ImportNote
{
    /// <summary>The command's own name is one of Inferpal's (or another imported one): renamed, the name in the detail.</summary>
    Renamed,
    /// <summary>A key Continue does not read in <c>config.yaml</c> (an old <c>config.json</c> key): imported as meant.</summary>
    IgnoredByContinue,
    /// <summary>A rule scoped to files: its scope is written in the text, which the model reads.</summary>
    ScopeInText,
    /// <summary>A Roo mode's tool groups have no equivalent: the command keeps its instructions only.</summary>
    ModeToolsNotKept,
    /// <summary>The server or the model changes: the editor takes it after a restart.</summary>
    TakesEffectAfterRestart,
    /// <summary>A secret the import could not find (<c>${{ secrets.X }}</c>): written as is, to fill in.</summary>
    SecretToFill,
    /// <summary>An allow rule lets commands run without asking: left unticked, the choice is the user's.</summary>
    RunsWithoutAsking,
    /// <summary>The command Continue wrote into every new configuration: left unticked, the user never wrote it.</summary>
    ContinueDefault,
}

/// <summary>A note under an item, with what it names (the command's original name…).</summary>
internal sealed record ImportItemNote(ImportNote Kind, string? Detail = null);

/// <summary>One thing the import can write, ticked by default unless <see cref="Checked"/> says otherwise.</summary>
/// <param name="Id">Stable across two readings of the same file: <c>/import … apply</c> re-reads the source.</param>
/// <param name="SourcePaths">The paths of the source this item carries (<c>models[0].model</c>).</param>
internal sealed record ImportItem(string Id, ImportItemKind Kind, ImportEffect Effect, IReadOnlyList<string> SourcePaths,
                                  bool Checked, IReadOnlyList<ImportItemNote> Notes);

/// <summary>Why a part of the source is not imported — each one named on the preview.</summary>
internal enum ImportSkipCause
{
    /// <summary><c>name</c>, <c>version</c>, <c>schema</c>: the identity of the file, not a setting.</summary>
    FileIdentity,
    /// <summary>A hosted provider (Anthropic, OpenRouter, Gemini…): Inferpal only talks to a server the user runs.</summary>
    HostedProvider,
    /// <summary>A model on another server than the one kept: Inferpal has one server.</summary>
    OtherServer,
    /// <summary>Another chat model: Inferpal lists every model of the server, the first becomes the default.</summary>
    OtherChatModel,
    /// <summary><c>AUTODETECT</c>: no model to write, Inferpal picks among the installed ones.</summary>
    Autodetect,
    /// <summary>A role the same model already fills (the chat model also edits): nothing to write.</summary>
    SameModelAsChat,
    /// <summary>A second model for one role (embedding, completion…): only the first is kept. The role in the detail.</summary>
    OneModelPerRole,
    /// <summary>A role Inferpal does not have (<c>apply</c>, <c>rerank</c>, <c>subagent</c>), named in the detail.</summary>
    RoleNotInInferpal,
    /// <summary>A display name: Inferpal shows the server's model name.</summary>
    DisplayName,
    /// <summary>Temperature, top-p…: Inferpal applies the values the model's publisher recommends.</summary>
    Sampling,
    /// <summary><c>maxTokens</c>: Inferpal does not cut an answer at a fixed length.</summary>
    ResponseLength,
    /// <summary>Declared capabilities (<c>tool_use</c>…): Inferpal finds them on the model itself.</summary>
    DeclaredCapabilities,
    /// <summary>Request options (timeout, proxy, headers of a model): no equivalent per model.</summary>
    RequestOptions,
    /// <summary>Autocomplete tuning: Inferpal's completion has its own.</summary>
    AutocompleteOptions,
    /// <summary>A model's own prompt templates (<c>apply</c>…): no equivalent.</summary>
    PromptTemplates,
    /// <summary>Context providers: Inferpal's @-mentions are built in.</summary>
    ContextProviders,
    /// <summary>A Hub block (<c>uses:</c>): the Hub is closed, the block cannot be read.</summary>
    HubBlock,
    /// <summary>Telemetry: Inferpal sends none.</summary>
    Telemetry,
    /// <summary>A secret the import could not find in Continue's <c>.env</c> files: to type in the settings.</summary>
    SecretNotFound,
    /// <summary>One of Continue's built-in commands, named in the detail.</summary>
    ContinueBuiltinCommand,
    /// <summary>Already in Inferpal (same server name, same site): not overwritten.</summary>
    AlreadyConfigured,
    /// <summary>Empty.</summary>
    Empty,
    /// <summary>A key the tool does not know (an anchor holder, a typo, another tool's key).</summary>
    UnknownKey,
    /// <summary>A setting of the tool with no equivalent in Inferpal.</summary>
    NoEquivalent,
    /// <summary>Roo: a model per mode — the profiles named are not kept.</summary>
    ModelPerMode,
    /// <summary>Roo: the mode it was in; Inferpal starts in agent mode.</summary>
    ActiveMode,
    /// <summary>Roo: automatic approval — Inferpal's approval is never turned off by an import.</summary>
    AutoApproval,
    /// <summary>Roo: the allowed commands did nothing, automatic approval of commands was off.</summary>
    AutoApprovalWasOff,
    /// <summary>Roo: condensing the conversation — Inferpal compacts it itself.</summary>
    Compaction,
    /// <summary>Roo: checkpoints — Inferpal backs up before every write (<c>/undo-run</c>).</summary>
    Checkpoints,
    /// <summary>Roo: codebase indexing was off, or its embedder is hosted.</summary>
    IndexingNotKept,
    /// <summary>Roo: its own catalogue (<c>codebaseIndexModels</c>), not a setting.</summary>
    ToolCatalogue,
    /// <summary>Roo: <c>whenToUse</c> serves Roo's automatic choice of mode.</summary>
    WhenToUse,
    /// <summary>A language Inferpal is not translated into, named in the detail.</summary>
    UnsupportedLanguage,
    /// <summary>Roo: the export carries no MCP server (they live in Roo's own file).</summary>
    NoServersInExport,
}

/// <summary>A part of the source the import does not write, and why.</summary>
internal sealed record ImportSkip(string Path, ImportSkipCause Cause, string? Detail = null);

/// <summary>Everything a source says, as the preview shows it: what can be written, what is not and why.</summary>
internal sealed record ImportPlan(ImportSource Source, string File, IReadOnlyList<ImportItem> Items,
                                  IReadOnlyList<ImportSkip> Skipped);
