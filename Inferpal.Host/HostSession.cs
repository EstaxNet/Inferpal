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

    /// <summary>Approval pipeline — §25 asks it once per `/tdd` run before any debug capture.</summary>
    public required IApprovalService     Approval     { get; init; }

    /// <summary>§25 capture port; null when the adapter did not declare `debug/*` support.</summary>
    public Services.Debugging.ITestDebugCapture? TestCapture { get; init; }

    /// <summary>Named-session persistence, same store (and files) as the VS extension.</summary>
    /// <summary>
    /// The connection heartbeat's state machine. One instance per session: it starts OPTIMISTIC, so
    /// the first successful check is silent and the first failed one announces.
    /// </summary>
    public ConnectionStatusPresenter Connection { get; } = new();

    public ConversationStore Store { get; } = new();

    /// <summary>
    /// Conversation history, seeded with the layered system prompt (index 0). Replacing it resets
    /// <see cref="LastPromptTokens"/> to zero, and that is not a refinement: the counter measures
    /// the size of the PREVIOUS turn's prompt, and the pre-send context check decides on it.
    /// Keeping the one from a conversation just left - <c>/clear</c>, loading a session, creating a
    /// branch - would compact a short conversation the user has only just opened: a model call for
    /// nothing, and turns thrown away.
    ///
    /// The ordinary turn reassigns both in the right order (history then counter), so it is not
    /// affected.
    /// </summary>
    public List<ChatMessageDto> History
    {
        get => _history;
        set { _history = value; LastPromptTokens = 0; }
    }

    private List<ChatMessageDto> _history = [];

    /// <summary>
    /// Prompt tokens the backend reported for the last turn - what the pre-send context check
    /// measures to decide whether to compact.
    /// </summary>
    /// <remarks>
    /// It did not exist, and that is why this host NEVER bounded its history: the conversation grew
    /// until it went past the model's <c>num_ctx</c>, and it was the backend that dropped its head -
    /// system prompt included - without a word. See <see cref="Services.Agent.ContextManager"/>.
    /// </remarks>
    public int LastPromptTokens { get; set; }

    /// <summary>Session file the conversation currently lives in (null = never saved, or reset).
    /// Mirror of the VS VM's <c>_currentSessionName</c>; <c>/branch</c> records it as the parent
    /// of a new branch. The <c>last_session</c> auto-save slot deliberately doesn't count.</summary>
    public string? CurrentSessionName { get; set; }

    /// <summary>Prompt-section ids switched off from the Context X-Ray panel (session-scoped;
    /// consumed by the system-prompt builder so the next turns skip those layers).</summary>
    public HashSet<string> XrayDisabledSections { get; } = [];

    /// <summary>Session-scoped `/tools on|off` switch (mirror of the VS VM's <c>_toolsEnabled</c>):
    /// when off, `chat/send` runs plain chat even if agent mode is configured on.</summary>
    public bool ToolsEnabled { get; set; } = true;

    /// <summary>System-prompt suffix of the active `/template` (mirror of the VS VM's
    /// <c>_activeTemplateSuffix</c>); appended by the host's system-prompt builder.</summary>
    public string? TemplateSuffix { get; set; }

    /// <summary>Plan mode (`/plan`): read-only tool registry + plan-mode prompt suffix.</summary>
    public bool PlanMode { get; set; }

    /// <summary>Plan the session is working on (mirror of the VS VM's <c>_activePlan</c>), so
    /// `/plan next` and `/plan done n` need no argument. Session state: the file is what
    /// persists, not which one happened to be open.</summary>
    public string? ActivePlan { get; set; }

    /// <summary>Agent step mode (`/agent-step`): pause after every tool call until
    /// `chat/resumeStep` (or `/resume`) releases <see cref="StepResume"/>.</summary>
    public bool StepMode { get; set; }

    /// <summary>Pending step-mode pause; null when the agent is not paused.</summary>
    public TaskCompletionSource<bool>? StepResume;

    private BackgroundTaskQueue? _tasks;

    /// <summary>
    /// Background agent tasks (<c>/task</c>), created on first use. The runner is supplied by the
    /// caller because it needs the live system prompt, and the queue must outlive the turn that
    /// submitted the task — it therefore never sees the turn's cancellation token.
    /// </summary>
    public BackgroundTaskQueue GetOrCreateTasks(
        BackgroundTaskQueue.TaskRunner runner, Action<BackgroundTaskSnapshot> onFinished)
    {
        if (_tasks is not null) return _tasks;
        _tasks = new BackgroundTaskQueue(runner);
        _tasks.TaskFinished += onFinished;
        return _tasks;
    }

    public void Dispose()
    {
        // Every child process this session spawned must die with it: on Windows a process started
        // with UseShellExecute=false survives its parent, so "they die with the host" was wishful
        // thinking — closing the editor left MCP servers and detached background shells running.
        try { Mcp.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { Diagnostics.Swallow("HostSession.DisposeMcp", ex); }

        try { Tools.Dispose(); }
        catch (Exception ex) { Diagnostics.Swallow("HostSession.DisposeTools", ex); }

        try { Lsp.Dispose(); }
        catch (Exception ex) { Diagnostics.Swallow("HostSession.DisposeLsp", ex); }

        // Detached agent runs must not survive the window that started them either.
        try { _tasks?.Dispose(); }
        catch (Exception ex) { Diagnostics.Swallow("HostSession.DisposeTasks", ex); }
    }
}
