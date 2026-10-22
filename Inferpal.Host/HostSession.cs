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
    /// <summary>The client of each role — completions and code search may run on a server of their own.</summary>
    public required RoleClients          Roles        { get; init; }
    public required OpenDocumentOverlay  Overlay      { get; init; }
    public required RpcEditorSurface     Editor       { get; init; }
    public required ToolRegistry         Tools        { get; init; }
    public required AgentOrchestrator    Orchestrator { get; init; }
    public required ProjectIndexService  Index        { get; init; }
    public required DocsIndexService     Docs         { get; init; }
    public required McpToolService       Mcp          { get; init; }
    public required LspSemanticProvider  Lsp          { get; init; }
    public required string               RootDir      { get; init; }

    private Services.Presentation.SettingsDocsActions? _docsActions;

    /// <summary>The settings page's @Docs actions, on this session's settings and docs index (and the reports of their
    /// crawls).</summary>
    /// <remarks>⚠ Held by the session, never by the server: kept across a new <c>initialize</c>, the page's actions went
    /// to the docs index of the session before — disposed — and read its settings.</remarks>
    public Services.Presentation.SettingsDocsActions DocsActions => _docsActions ??= new(Config, Docs);

    /// <summary>
    /// The settings file as this session last took it — what <c>config/reload</c> compares the file with, so that only
    /// what the file changed since is followed, never what this session changed in memory.
    /// </summary>
    public required System.Text.Json.Nodes.JsonObject FileSettings { get; set; }

    /// <summary>The editor as the model is told it (<see cref="InitializeParams.Editor"/>).</summary>
    public required string               EditorName   { get; init; }

    /// <summary>Every tool call is announced to the adapter when it starts and ends (<see cref="InitializeParams.ToolEvents"/>).</summary>
    public bool ToolEvents { get; init; }

    /// <summary>The model's reasoning goes to the adapter as it streams (<see cref="InitializeParams.ReasoningChunks"/>).</summary>
    public bool ReasoningChunks { get; init; }

    /// <summary>A compaction goes to the adapter as <c>chat/compaction</c> (<see cref="InitializeParams.CompactionEvents"/>).</summary>
    public bool CompactionEvents { get; init; }

    /// <summary>The host indexes the folder itself (<see cref="InitializeParams.IndexesTheFolder"/>); false: it only
    /// shows the index saved for it, whatever setting is saved — a pass starts on the page's Rebuild only.</summary>
    public bool IndexesTheFolder { get; init; } = true;

    /// <summary>The adapter keeps the workspace's last conversation in VS Code's auto-save slot
    /// (<see cref="InitializeParams.AutoSaveSlot"/>); false: this session never empties it.</summary>
    public bool KeepsAutoSave { get; init; } = true;

    /// <summary>
    /// Folders open in the editor that <see cref="RootDir"/> does not hold — stated to the model with the root. Replaced
    /// whole by <c>workspace/folders</c> and read once per prompt build, so the next question already has it.
    /// </summary>
    public IReadOnlyList<string> FoldersOutOfReach { get; set; } = [];

    /// <summary>Approval pipeline — `/tdd` asks it once per run before any debug capture.</summary>
    public required IApprovalService     Approval     { get; init; }

    /// <summary>`/tdd` debugger capture port; null when the adapter did not declare `debug/*` support.</summary>
    public Services.Debugging.ITestDebugCapture? TestCapture { get; init; }

    /// <summary>
    /// The adapter's debugger, same nullability rule as <see cref="TestCapture"/>. Held here — not
    /// only handed to the tool registry — because the <c>@debugger</c> mention asks the same
    /// question as <c>get_debugger_state</c> and must tell "nothing is paused" apart from an answer,
    /// which reading a tool's returned sentence cannot do.
    /// </summary>
    public Services.Debugging.IDebugSession? Debug { get; init; }

    /// <summary>
    /// The connection heartbeat's state machine. One instance per session: it starts OPTIMISTIC, so
    /// the first successful check is silent and the first failed one announces.
    /// </summary>
    public ConnectionStatusPresenter Connection { get; } = new();

    /// <summary>The <c>/arena</c> battle this conversation showed: one per session, an ACP host serving several threads.</summary>
    public Services.Commands.ArenaShownBattle Arena { get; } = new();

    /// <summary>Named-session persistence, same store (and files) as the VS extension.</summary>
    public ConversationStore Store { get; } = new(slotOwner: ConversationStore.VsCodeSlot);

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
    /// <remarks>
    /// ⚠ The NEW conversation's own measure, not zero: zero is a first turn's state, so the pre-send check let the
    /// first question after a reload go out uncompacted — and a reload is large (every turn and tool output the screen
    /// kept, 1.9× to 8.9× the history the model had live on real sessions). Estimated like the ordinary turn does.
    /// </remarks>
    public List<ChatMessageDto> History
    {
        get => _history;
        set { _history = value; LastPromptTokens = Services.Agent.AgentOrchestrator.EstimateTokens(value); }
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

    /// <summary>The window the last context check measured against (0 until one ran).</summary>
    public int LastContextWindow { get; set; }

    /// <summary>
    /// The window every fill indicator shows (X-Ray, the gauge): the one the last turn was measured
    /// against — the loaded one when the server reports a smaller one — else the configured one.
    /// </summary>
    public int ContextWindowInUse => LastContextWindow > 0 ? LastContextWindow : Config.ContextWindowSize;

    /// <summary>
    /// Session summary written every <c>OodaTurnThreshold</c> turns (mirror of the VS VM's
    /// <c>_oodaSummary</c>). The system-prompt builder appends it, so a prompt rebuild keeps it; a new
    /// conversation leaves it behind.
    /// </summary>
    public string? OodaSummary { get; set; }

    /// <summary>The session summary the system message carries: <see cref="OodaSummary"/> as it was when the history was
    /// last compacted (mirror of the VS VM's <c>_recapInSystem</c>).</summary>
    /// <remarks>⚠ Never the newest one in between: each summary written into the head of the request would make the
    /// server read the whole conversation again.</remarks>
    public string? RecapInSystem { get; set; }

    /// <summary>File the adapter last reported active (`editor/didChangeActiveDocument`): the glob-scoped
    /// project rules of the system prompt follow it (mirror of the VS VM's <c>_activeFilePath</c>).</summary>
    public string? ActiveFilePath { get; set; }

    /// <summary>Language of the last active CODE file (mirror of the VS VM's <c>_personaLanguage</c>): the
    /// persona the system prompt carries when persona auto-switching is on.</summary>
    public string? PersonaLanguage { get; set; }

    /// <summary>Finished turns of this conversation (mirror of the VS VM's <c>_conversationTurnCount</c>).</summary>
    public int ConversationTurnCount { get; set; }

    /// <summary>The workspace block was sent with this conversation's first question (mirror of the VS VM's
    /// <c>_workspaceContextInjected</c>): set only once a non-empty block went out.</summary>
    public bool WorkspaceContextSent { get; set; }

    /// <summary>Session file the conversation currently lives in (null = never saved, or reset).
    /// Mirror of the VS VM's <c>_currentSessionName</c>; <c>/branch</c> records it as the parent
    /// of a new branch. The <c>last_session</c> auto-save slot deliberately doesn't count.</summary>
    public string? CurrentSessionName
    {
        get => _currentSessionName;
        set { _currentSessionName = value; SessionNameKnown = true; }
    }
    private string? _currentSessionName;

    /// <summary>The name above was set by this process (a load, a save, a branch, a new conversation). A host started
    /// again knows nothing yet: see <see cref="SessionManager.AutoSaveName"/>.</summary>
    public bool SessionNameKnown { get; private set; }

    /// <summary>Prompt-section ids switched off from the Context X-Ray panel (session-scoped;
    /// consumed by the system-prompt builder so the next turns skip those layers).</summary>
    public HashSet<string> XrayDisabledSections { get; } = [];

    /// <summary>Session-scoped `/tools on|off` switch (mirror of the VS VM's <c>_toolsEnabled</c>):
    /// when off, `chat/send` runs plain chat even if agent mode is configured on.</summary>
    public bool ToolsEnabled { get; set; } = true;

    /// <summary>System-prompt suffix of the active `/template` (mirror of the VS VM's
    /// <c>_activeTemplateSuffix</c>); appended by the host's system-prompt builder.</summary>
    public string? TemplateSuffix { get; set; }

    /// <summary>The <c>/template</c> mode of the conversation just discarded: the adapter archives that conversation
    /// AFTER the reset (<c>session/archive</c>), when <see cref="TemplateSuffix"/> is already the new one's.</summary>
    public string? DiscardedTemplateSuffix { get; set; }

    /// <summary>The archive the last reset made from the auto-save slot, and when: the adapter's <c>session/archive</c>
    /// writes over it — one archive per discarded conversation — and its title renames it.</summary>
    public (string Name, DateTime At)? PendingArchive { get; set; }

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

    /// <summary>The notes of the turn running now (<c>chat/steer</c>); null when no turn with steps runs.</summary>
    public volatile RunSteering? Steering;

    private BackgroundTaskQueue? _tasks;

    /// <summary>
    /// Background agent tasks (<c>/task</c>), created on first use. The runner is supplied by the
    /// caller because it needs the live system prompt, and the queue must outlive the turn that
    /// submitted the task — it therefore never sees the turn's cancellation token.
    /// </summary>
    public BackgroundTaskQueue GetOrCreateTasks(
        BackgroundTaskQueue.ProposingTaskRunner runner, Action<BackgroundTaskSnapshot> onFinished,
        Func<string?>? submissionContext = null)
    {
        if (_tasks is not null) return _tasks;
        // The session's root never changes (another folder is another host): given for the same rule as Visual Studio's.
        _tasks = new BackgroundTaskQueue(runner, currentRoot: () => RootDir, submissionContext: submissionContext);
        _tasks.TaskFinished += onFinished;
        return _tasks;
    }

    /// <summary>The background tasks not finished yet — they die with this process.</summary>
    public IReadOnlyList<BackgroundTaskSnapshot> UnfinishedTasks => _tasks?.List() ?? [];

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

        // ⚠ The code index watches the folder and re-embeds what changes: kept, every thread an ACP agent closed — to
        // make room, or for good — left one more indexer on the same folder, each save embedded once per dead thread.
        try { Index.Dispose(); }
        catch (Exception ex) { Diagnostics.Swallow("HostSession.DisposeIndex", ex); }

        // Detached agent runs must not survive the window that started them either.
        try { _tasks?.Dispose(); }
        catch (Exception ex) { Diagnostics.Swallow("HostSession.DisposeTasks", ex); }
    }
}
