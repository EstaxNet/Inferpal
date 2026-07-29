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

/// <summary>`command/slash` — a chat input starting with <c>/</c>. The host executes the
/// commands it can serve headlessly; <c>Handled = false</c> tells the adapter to send the
/// text as a normal chat prompt instead.</summary>
internal sealed record SlashCommandParams(string Text);

/// <summary>`command/slash` answer: <paramref name="Markdown"/> is the bubble to render
/// when <paramref name="Handled"/> is true.</summary>
internal sealed record SlashCommandResult(bool Handled, string? Markdown = null);

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

// ── Sessions (persisted in %AppData%/Inferpal/sessions/, shared with the VS extension) ──

/// <summary>One display message of a saved session (wire mirror of <c>SavedMessage</c>).</summary>
internal sealed record SavedMessageDto(string Role, string Content, string? ToolName = null, string? Timestamp = null);

/// <summary>`session/save` — persists the adapter's transcript under <paramref name="Name"/>.</summary>
internal sealed record SessionSaveParams(string Name, List<SavedMessageDto> Messages);

/// <summary>`session/load` / `session/delete` argument.</summary>
internal sealed record SessionRefParams(string Name);

/// <summary>`session/list` entry.</summary>
internal sealed record SessionSummaryDto(string Name, DateTime SavedAt, int MessageCount, string Preview);

/// <summary>`session/load` answer: the transcript to re-render (host history already rebuilt).</summary>
internal sealed record SessionLoadResult(string Name, List<SavedMessageDto> Messages);
