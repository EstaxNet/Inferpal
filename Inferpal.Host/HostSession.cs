using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Docs;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Persistence;
using Inferpal.Services.Rag;

namespace Inferpal.Host;

/// <summary>
/// Service graph built by `initialize` — the host-side equivalent of the DI registrations
/// in <c>InferpalExtension.InitializeServices</c>, plus the conversation history the VS VM
/// keeps for itself. One session per host process (the adapter spawns one host per window).
/// </summary>
internal sealed class HostSession : IDisposable
{
    public required InferpalConfig       Config       { get; init; }
    public required IInferenceProvider   Client       { get; init; }
    public required OpenDocumentOverlay  Overlay      { get; init; }
    public required RpcEditorSurface     Editor       { get; init; }
    public required ToolRegistry         Tools        { get; init; }
    public required AgentOrchestrator    Orchestrator { get; init; }
    public required ProjectIndexService  Index        { get; init; }
    public required DocsIndexService     Docs         { get; init; }
    public required McpToolService       Mcp          { get; init; }
    public required LspSemanticProvider  Lsp          { get; init; }
    public required string               RootDir      { get; init; }

    /// <summary>Named-session persistence, same store (and files) as the VS extension.</summary>
    public ConversationStore Store { get; } = new();

    /// <summary>Conversation history, seeded with the layered system prompt (index 0).</summary>
    public List<ChatMessageDto> History { get; set; } = [];

    /// <summary>Prompt-section ids switched off from the Context X-Ray panel (session-scoped;
    /// consumed by the system-prompt builder so the next turns skip those layers).</summary>
    public HashSet<string> XrayDisabledSections { get; } = [];

    /// <summary>Session-scoped `/tools on|off` switch (mirror of the VS VM's <c>_toolsEnabled</c>):
    /// when off, `chat/send` runs plain chat even if agent mode is configured on.</summary>
    public bool ToolsEnabled { get; set; } = true;

    /// <summary>System-prompt suffix of the active `/template` (mirror of the VS VM's
    /// <c>_activeTemplateSuffix</c>); appended by the host's system-prompt builder.</summary>
    public string? TemplateSuffix { get; set; }

    public void Dispose()
    {
        // McpToolService owns no disposable state (its stdio clients die with the process).
        try { Lsp.Dispose(); }
        catch (Exception ex) { Diagnostics.Swallow("HostSession.DisposeLsp", ex); }
    }
}
