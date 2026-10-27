using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Persistence;
using Inferpal.Services.Tools;
using NonConcurrentSynchronizationContext = Microsoft.VisualStudio.Threading.NonConcurrentSynchronizationContext;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace Inferpal.Host.Acp;

/// <summary>
/// One ACP session: a <see cref="HostServer"/> of its own, driven over an in-memory pipe exactly as the VS Code extension
/// drives one (<c>initialize</c>, <c>chat/send</c>, <c>command/slash</c>…), and what it says translated into
/// <c>session/update</c> notifications.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ No logic of its own: the turn, the context check, the tools, the approvals, the persistence are the host's. What
/// lives here is translation, and the few things ACP has no wire for, which are SAID rather than dropped.
/// </para>
/// <para>
/// ⚠ Order is the contract. The host's notifications are handled one at a time, in the order they arrive (the link's
/// non-concurrent synchronization context), and everything sent to the client goes through the agent's single queue —
/// a tool call before the permission it raises, every update before the response that ends the turn.
/// </para>
/// </remarks>
internal sealed class AcpSession : IAsyncDisposable
{
    /// <summary>The commands that need an editor: VS Code serves them itself, on its editor or its panels, and the host
    /// serves <c>/test</c> on the editor's active file — none of which an ACP client gives the agent. <c>/xray</c> and
    /// <c>/export</c> are not among them: the host renders both as text.</summary>
    internal static readonly HashSet<string> EditorOnlyCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "/explain", "/review", "/fix", "/refactor", "/doc", "/branch", "/agent-step", "/resume", "/test",
    };

    /// <summary>The most files a message attaches; the others are named.</summary>
    internal const int MaxAttachedFiles = 5;

    private readonly AcpAgent _agent;
    private readonly HostServer _host;
    private readonly JsonRpc _hostSide;
    private readonly JsonRpc _link;
    private readonly NonConcurrentSynchronizationContext _linkContext = new(sticky: false);
    private readonly bool _fsRead;

    private readonly List<SavedMessageDto> _transcript = [];
    private readonly List<string> _prompts = [];
    private readonly List<(string Name, string Content)> _pending = [];
    private readonly ConcurrentDictionary<string, ToolCallState> _calls = new();
    private readonly List<PlanEntryState> _plan = [];
    private readonly object _gate = new();

    private sealed class ToolCallState
    {
        public required string Name;
        /// <summary>The title the client draws ("read_file · alpha.txt"): what the saved line keeps, so a replay draws it too.</summary>
        public required string Title;
        /// <summary>Who the call is (<see cref="AcpToolCalls.CallKey"/>): its report from the loop is matched on it.</summary>
        public required string Key;
        public List<object> Diffs = [];
        public bool Rejected;
    }

    private sealed record PlanEntryState(string Content, string Status);

    private string _mode = AcpModes.Default;
    private string? _model;
    /// <summary>The tokens this thread's turns used since it opened, for <c>/export</c>'s stats, as VS Code adds them up.</summary>
    private int _sessionTokens;
    private string _defaultModel = string.Empty;
    private List<string> _models = [];
    private AcpShownStream _stream = new();

    /// <summary>
    /// This turn runs the agent's plan, whose host keeps as the answer only what the model writes after its last reset
    /// (<c>chat/streamReset</c>): the stream is shown as the run's thinking, and the answer is said once, at the end.
    /// </summary>
    /// <remarks>⚠ A client cannot take back a message chunk. Sent as the answer, the text the host discards — an answer
    /// given before the tool the plan asked for, a plan written again — stayed on screen, and the answer that followed
    /// showed it twice; the session, which saved everything sent, reloaded it twice too.</remarks>
    private bool _provisional;
    private bool _actBreak;
    /// <summary>What the current wait has said as a thought: 0 nothing, 1 that it reads, 2 that it runs longer.</summary>
    private int _waitSaid;
    private bool _turnOpen;
    private bool _cancelRequested;
    /// <summary>The calls drawn by <c>chat/toolStart</c> that ended in this turn, whose report from the loop has not come
    /// yet: tool name and <see cref="AcpToolCalls.CallKey"/>. Under <see cref="_gate"/>.</summary>
    private readonly List<(string Name, string Key)> _ended = [];
    private int _approvals;
    private int _replayed;

    /// <summary>The client has this session's id: it loaded or resumed it, or sent a prompt naming it.</summary>
    private volatile bool _known;

    /// <summary>An agreement was asked before the client knew this session (1): asked again at the next prompt.</summary>
    private int _askAgain;

    private AcpSession(AcpAgent agent, string id, string cwd, HostServer host, JsonRpc hostSide, JsonRpc link, bool fsRead,
                       bool known)
    {
        _known    = known;
        _agent    = agent;
        Id        = id;
        Cwd       = cwd;
        _host     = host;
        _hostSide = hostSide;
        _link     = link;
        _fsRead   = fsRead;
    }

    public string Id { get; }
    public string Cwd { get; }

    /// <summary>When this session last ran a turn — the agent closes the idle ones first.</summary>
    public DateTime LastUsed { get; private set; } = DateTime.UtcNow;

    /// <summary>A turn is running.</summary>
    public bool Busy { get { lock (_gate) return _turnOpen; } }

    /// <summary>A turn is running, or work that runs beyond one — a <c>/task</c>, a command in the background, an
    /// <c>@Docs</c> crawl —, which dies with this session's host (<c>host/runningWork</c>, the one reader).</summary>
    public bool Working => Busy || _host.RunningWork().Notice is not null;

    /// <summary>Completed when no turn runs; replaced at each turn's start.</summary>
    private TaskCompletionSource _idle = Completed();

    private static TaskCompletionSource Completed()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }

    /// <summary>Waits, at most <paramref name="grace"/>, for the running turn to end.</summary>
    public async Task WhenIdleAsync(TimeSpan grace)
    {
        Task idle;
        lock (_gate) idle = _idle.Task;
        try { await idle.WaitAsync(grace); }
        catch (TimeoutException) { Diagnostics.Record("Acp", $"Session {Id}: its turn did not end within {grace.TotalSeconds:0} s of the close."); }
    }

    // ── Start ──────────────────────────────────────────────────────────────────

    /// <summary>Starts the session's host on <paramref name="cwd"/> and hands it the editor's MCP servers.</summary>
    /// <param name="known">The client already has the id (a load, a resume, a session reopened after eviction) — not
    /// for <c>session/new</c>, whose response is what tells it.</param>
    public static async Task<AcpSession> StartAsync(AcpAgent agent, string id, string cwd,
                                                    IReadOnlyList<McpSessionServerDto> mcpServers, CancellationToken ct,
                                                    bool known = false)
    {
        var (hostStream, linkStream) = FullDuplexStream.CreatePair();
        var host     = agent.CreateHost();
        var hostSide = HostRpc.Create(hostStream, hostStream, host);
        host.Attach(hostSide);
        hostSide.StartListening();

        var link    = HostRpc.Create(linkStream, linkStream);
        var session = new AcpSession(agent, id, cwd, host, hostSide, link, agent.ClientReadsFiles, known);
        link.SynchronizationContext = session._linkContext;
        link.AddLocalRpcTarget(session, new JsonRpcTargetOptions { DisposeOnDisconnect = false });
        link.StartListening();

        try
        {
            var init = await link.InvokeWithParameterObjectAsync<InitializeResult>("initialize", new
            {
                rootDir         = cwd,
                clientName      = agent.ClientName,
                editor          = agent.EditorName,
                editorSurface   = false,
                bufferProbe     = agent.ClientReadsFiles,
                toolEvents      = true,
                reasoningChunks = true,
                secrets         = false,
                // A thread lives in its own session file: VS Code's last conversation in this folder is not its own.
                autoSaveSlot    = false,
                compactionEvents = true,
            }, ct);
            session._defaultModel = init.DefaultModel;
            await session.AdoptDefaultModelAsync(ct);
            if (mcpServers.Count > 0)
                await link.InvokeWithParameterObjectAsync("mcp/sessionServers", new { servers = mcpServers }, ct);
            session._mode = await session.ConfiguredModeAsync(ct);
            await session.RefreshModelsAsync(ct);
            await session.RefreshModesAsync(ct);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>What a session's start had to say, told at its first turn (the client did not know the session yet).</summary>
    private string? _startNotice;

    /// <summary>
    /// The default model, when nobody chose it and the server does not have it, replaced by the best one installed —
    /// as VS Code does at start (<c>models/adoptDefault</c>), and said. Without it, a fresh machine with an Ollama
    /// already running asked every question of a model that is not there.
    /// </summary>
    private async Task AdoptDefaultModelAsync(CancellationToken ct)
    {
        try
        {
            var adopted = await _link.InvokeWithCancellationAsync<ModelsAdoptResult>("models/adoptDefault", cancellationToken: ct);
            if (adopted.Model is { Length: > 0 } model) _defaultModel = model;
            _startNotice = adopted.Notice;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Diagnostics.Swallow("AcpSession.AdoptDefaultModel", ex); }
    }

    /// <summary>
    /// The mode a new session opens in — the agent when the user turned agent mode on, else the default — and the model
    /// set for the agent's role, which answers in Agent mode.
    /// </summary>
    private async Task<string> ConfiguredModeAsync(CancellationToken ct)
    {
        var mode = AcpModes.Default;
        try
        {
            var json = await _link.InvokeWithCancellationAsync<string>("config/get", cancellationToken: ct);
            using var doc = JsonDocument.Parse(json);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(p.Name, "agentModeEnabled", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.True)
                    mode = AcpModes.Agent;
                else if (string.Equals(p.Name, "agentModel", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                    _agentRoleModel = p.Value.GetString()!.Trim();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Diagnostics.Swallow("AcpSession.ConfiguredMode", ex); }
        return mode;
    }

    /// <summary>The settings' default model (unless one was picked in this thread) and the agent's role model, read again.</summary>
    private async Task ReadConfiguredModelsAsync(CancellationToken ct)
    {
        try
        {
            var json = await _link.InvokeWithCancellationAsync<string>("config/get", cancellationToken: ct);
            using var doc = JsonDocument.Parse(json);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (string.Equals(p.Name, "agentModel", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                    _agentRoleModel = p.Value.GetString()!.Trim();
                else if (_model is null && string.Equals(p.Name, "defaultModel", StringComparison.OrdinalIgnoreCase)
                         && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } model)
                    _defaultModel = model;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Diagnostics.Swallow("AcpSession.ReadConfiguredModels", ex); }
    }

    /// <summary>The server's last listing, split by what each model can be picked for; <c>null</c> until one answered.</summary>
    private ModelsByKind? _listed;

    /// <summary>The installed chat models (a model that answers no question is left out), read again on demand.</summary>
    private async Task RefreshModelsAsync(CancellationToken ct)
    {
        try
        {
            var r = await _link.InvokeWithParameterObjectAsync<ModelsByKindResult>("models/listByKind", new { }, ct);
            _listed = new ModelsByKind(r.Listed ?? [.. r.Chat, .. r.Embedding], r.Chat, r.Embedding);
            _models = [.. _listed.Chat.Distinct(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The server may be down: the model the settings name is still offered, and the turn will say why it fails.
            Diagnostics.Swallow("AcpSession.Models", ex);
            _listed = null;
            _models = [];
        }
    }

    /// <summary>The modes found for the workspace (<c>mode/list</c>), offered beside Inferpal's own.</summary>
    private IReadOnlyList<AcpModes.Found> _found = [];

    /// <summary>The workspace's modes, read again: <c>true</c> when the list changed.</summary>
    private async Task<bool> RefreshModesAsync(CancellationToken ct)
    {
        try
        {
            var r = await _link.InvokeWithParameterObjectAsync<ModeListResult>("mode/list", new { }, ct);
            var found = r.Modes.Select(AcpModes.Found.Of).ToList();
            if (found.SequenceEqual(_found)) return false;
            _found = found;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Swallow("AcpSession.Modes", ex);
            return false;
        }
    }

    // ── Modes and options ───────────────────────────────────────────────────────

    /// <summary>The model set for the agent's role (<c>agentModel</c>); empty when the chat model does the agent's work.</summary>
    private string _agentRoleModel = string.Empty;

    /// <summary>The model the client's selector last showed.</summary>
    private string? _announcedModel;

    /// <summary>
    /// The model the next turn asks, as the client's selector shows it: the one picked in this thread — it answers in
    /// every mode —, else, in Agent mode, the model set for the agent's role, else the settings' default.
    /// </summary>
    /// <remarks>⚠ The host routes an Agent-mode turn to the agent's role model, over the chat model: the selector showed
    /// the chat model while another answered, and a model picked in it changed nothing in Agent mode.</remarks>
    private string CurrentModel =>
        _model ?? (_found.FirstOrDefault(f => f.Id == _mode)?.ModelAmong(_models) is { } modeModel ? modeModel
                   : _mode == AcpModes.Agent && _agentRoleModel.Length > 0 ? _agentRoleModel : _defaultModel);

    public object ModeState() => AcpModes.State(_mode, _found);

    /// <summary>The options as the client is about to show them: the model they name is the one it now shows.</summary>
    public List<object> ConfigOptions()
    {
        _announcedModel  = CurrentModel;
        _announcedModels = [.. _models];
        return AcpModes.ConfigOptions(_mode, _announcedModel, _models, _found);
    }

    public async Task SetModeAsync(string modeId, CancellationToken ct)
    {
        if (AcpModes.IsFound(modeId))
        {
            // A mode found for the workspace: the host holds it (leaving Plan), and says the model it answers with.
            var set = await _link.InvokeWithParameterObjectAsync<ModeSetResult>(
                "mode/set", new { name = modeId[AcpModes.FoundPrefix.Length..] }, ct);
            if (set.Mode is null) throw AcpRpc.Error(AcpRpc.InvalidParams, set.Refused ?? $"Unknown mode '{modeId}'.");
            _mode = AcpModes.FoundPrefix + set.Mode.Name;   // its name as found, whatever case was asked
            await RefreshModesAsync(ct);
            return;
        }
        if (!AcpModes.Ids.Contains(modeId)) throw AcpRpc.Error(AcpRpc.InvalidParams, $"Unknown mode '{modeId}'.");
        if (AcpModes.IsFound(_mode))
            await _link.InvokeWithParameterObjectAsync<ModeSetResult>("mode/set", new { name = (string?)null }, ct);
        await _link.InvokeWithParameterObjectAsync<bool>("plan/mode", new { enabled = modeId == AcpModes.Plan }, ct);
        _mode = modeId;
    }

    /// <summary><c>/model &lt;name&gt;</c> in a thread: the selector's pick, for this thread only — refused as the selector
    /// refuses, and said why.</summary>
    private async Task<string> PickModelAsync(string typed, string picked)
    {
        // The list read again, within the editors' budget: a server that does not answer held the pick for the client's
        // whole timeout. Out of time, the pick is judged on the lists the thread has.
        using (var budget = new CancellationTokenSource(Services.Commands.ModelsCommandHandler.SwitchListBudget))
        {
            try { await RefreshModelsAsync(budget.Token); }
            catch (OperationCanceledException) { }
        }
        if (Services.Commands.ModelsCommandHandler.RefusedForChat(picked, _listed) is { } notForChat) return SayNotice(typed, notForChat);
        try
        {
            await SetConfigOptionAsync(AcpModes.ModelOption, JsonSerializer.SerializeToElement(picked), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Swallow("AcpSession.PickModel", ex);
            return SayNotice(typed, Strings.SlashModelNotServed(picked));
        }
        Update(new { sessionUpdate = "config_option_update", configOptions = ConfigOptions() });
        return SayNotice(typed, Strings.SlashModelChanged(picked));
    }

    public async Task<List<object>> SetConfigOptionAsync(string configId, JsonElement value, CancellationToken ct)
    {
        var chosen = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        switch (configId)
        {
            case AcpModes.ModeOption:
                await SetModeAsync(chosen ?? string.Empty, ct);
                break;
            case AcpModes.ModelOption:
                var offered = AcpModes.ModelChoices(CurrentModel, _models);
                if (chosen is not null && !offered.Contains(chosen, StringComparer.Ordinal))
                {
                    // Installed since the list was read (the server was down when the thread opened, a model was pulled):
                    // read again before refusing.
                    await RefreshModelsAsync(ct);
                    offered = AcpModes.ModelChoices(CurrentModel, _models);
                }
                if (chosen is null || !offered.Contains(chosen, StringComparer.Ordinal))
                    throw AcpRpc.Error(AcpRpc.InvalidParams, $"Unknown model '{chosen}'.");
                // The window's model for this session, as VS Code's per-window pick: every reader that falls back to the
                // default model (titles, summaries) answers with it too. Never saved: the other editors keep theirs.
                await _link.InvokeWithParameterObjectAsync("models/useForSession", new { model = chosen }, ct);
                _model = chosen;
                break;
            default:
                throw AcpRpc.Error(AcpRpc.InvalidParams, $"Unknown configuration option '{configId}'.");
        }
        return ConfigOptions();
    }

    /// <summary>
    /// What the client still shows of a session the agent closed to make room, and expects back at its next request: the
    /// mode, the model picked in the selector, the files attached for the next prompt.
    /// </summary>
    internal sealed record Kept(string Mode, string? Model, IReadOnlyList<(string Name, string Content)> Pending);

    internal Kept KeptForReopen()
    {
        lock (_gate) return new Kept(_mode, _model, [.. _pending]);
    }

    /// <summary>A session reopened after it was closed to make room takes back what its client still shows.</summary>
    /// <remarks>⚠ Reopened from its saved conversation alone, it came back in the mode a new session opens in, on the
    /// default model, its attachments gone — while the client still showed Plan and the picked model: the next request
    /// ran with the write tools, in a thread the user believed read-only.</remarks>
    internal async Task RestoreAsync(Kept kept, CancellationToken ct)
    {
        if (kept.Mode != _mode) await HoldAgainAsync(kept.Mode, ct);
        if (kept.Model is not null && kept.Model != _model)
        {
            await _link.InvokeWithParameterObjectAsync("models/useForSession", new { model = kept.Model }, ct);
            _model = kept.Model;
        }
        lock (_gate) _pending.AddRange(kept.Pending);
        _announcedModel = CurrentModel;
    }

    /// <summary>A mode the thread was in, taken again (a reopened or reloaded thread): one whose file is gone since leaves
    /// the thread in Default — never a thread that cannot open.</summary>
    private async Task HoldAgainAsync(string modeId, CancellationToken ct)
    {
        try { await SetModeAsync(modeId, ct); }
        catch (Exception ex) when (AcpModes.IsFound(modeId) && ex is not OperationCanceledException)
        {
            Diagnostics.Swallow("AcpSession.HoldAgain", ex);
            await SetModeAsync(AcpModes.Default, ct);
        }
    }

    /// <summary>A mode changed from inside the conversation (<c>/plan</c>): the client is told both ways.</summary>
    private void ModeChanged(string mode)
    {
        _mode = mode;
        Update(new { sessionUpdate = "current_mode_update", currentModeId = mode });
        Update(new { sessionUpdate = "config_option_update", configOptions = ConfigOptions() });
    }

    private int _commandsAnnounced;

    /// <summary>
    /// The commands, once per session: whichever comes first — the agent's announcement after <c>session/new</c>, a
    /// load, or the first turn (ahead of its own updates).
    /// </summary>
    /// <remarks>⚠ Sent late, the announcement could land after a turn's response — an update outside any turn, which
    /// the protocol forbids after a cancelled prompt.</remarks>
    public Task AnnounceCommandsOnceAsync(CancellationToken ct) =>
        Interlocked.Exchange(ref _commandsAnnounced, 1) == 0 ? SendAvailableCommandsAsync(ct) : Task.CompletedTask;

    /// <summary>The slash commands the client offers: the host's, less the ones only an editor can serve.</summary>
    private async Task SendAvailableCommandsAsync(CancellationToken ct)
    {
        try
        {
            var list = await _link.InvokeWithCancellationAsync<List<SlashCommandInfoDto>>("command/list", cancellationToken: ct);
            var commands = (list ?? [])
                .Where(c => c.Command.StartsWith('/') && !EditorOnlyCommands.Contains(c.Command) && !IsClear(c.Command))
                .Select(c => new { name = c.Command[1..], description = string.IsNullOrWhiteSpace(c.Hint) ? c.Command : c.Hint })
                .ToList();
            Update(new { sessionUpdate = "available_commands_update", availableCommands = commands });
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Diagnostics.Swallow("AcpSession.Commands", ex); }
    }

    private static bool IsClear(string command) => string.Equals(command, "/clear", StringComparison.OrdinalIgnoreCase);

    // ── A turn ─────────────────────────────────────────────────────────────────

    /// <summary><c>session/prompt</c>: the turn, and the reason it stopped.</summary>
    /// <param name="stopped">A Stop came for this prompt before its turn could open (its session was being reopened):
    /// the turn answers <c>cancelled</c> without reaching the host.</param>
    public async Task<string> PromptAsync(JsonElement prompt, bool stopped = false)
    {
        var content = AcpPromptBlocks.Read(prompt);
        lock (_gate)
        {
            if (_turnOpen) throw AcpRpc.Error(AcpRpc.InvalidParams, "A prompt is already running in this session.");
            _turnOpen = true;
            _waitSaid = 0;
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _cancelRequested = stopped;
            _stream = new AcpShownStream();
            _plan.Clear();
            _calls.Clear();
            _ended.Clear();
        }
        LastUsed = DateTime.UtcNow;
        _known = true;
        try
        {
            await AnnounceCommandsOnceAsync(CancellationToken.None);
            await ReadModelsAgainAsync();
            // The workspace's modes, read again: a mode file added or removed since shows in the client's list — and a
            // held mode whose file is gone leaves the selector (the host says so as the question runs).
            if (await RefreshModesAsync(CancellationToken.None))
            {
                if (AcpModes.IsFound(_mode) && !_found.Any(f => f.Id == _mode)) ModeChanged(AcpModes.Default);
                else Update(new { sessionUpdate = "config_option_update", configOptions = ConfigOptions() });
            }
            if (Interlocked.Exchange(ref _startNotice, null) is { Length: > 0 } notice) Notice("warning", notice, notice + "\n\n");
            await FollowSettingsAsync();
            await AskAgainWhatWasAskedTooEarlyAsync();
            AnnounceModelIfChanged();
            var stop = await RunTurnAsync(content);
            return CancelRequested ? "cancelled" : stop;
        }
        catch (Exception ex) when (CancelRequested && ex is OperationCanceledException or LocalRpcException
                                       or RemoteInvocationException or ConnectionLostException)
        {
            // Cancelled, the turn answers "cancelled" whatever stopped it — never an error.
            return "cancelled";
        }
        finally
        {
            // The host's last notifications are handled, then every update is on the wire — before the response.
            await DrainLinkAsync();
            TaskCompletionSource idle;
            lock (_gate) { _turnOpen = false; idle = _idle; }
            await _agent.FlushAsync();
            LastUsed = DateTime.UtcNow;
            idle.TrySetResult();
        }
    }

    /// <summary>
    /// Before each turn, the settings saved since — in the browser settings window, by another editor — are this
    /// thread's (<c>config/reload</c>). A model picked in this thread stays its own; a new model server is said, since
    /// the thread keeps talking to the one it started with.
    /// </summary>
    private async Task FollowSettingsAsync()
    {
        ConfigReloadResult reloaded;
        try
        {
            reloaded = await _link.InvokeWithCancellationAsync<ConfigReloadResult>("config/reload", cancellationToken: CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Swallow("AcpSession.FollowSettings", ex);
            return;
        }
        if (reloaded.Taken.Contains("defaultModel"))
        {
            _defaultModel = reloaded.DefaultModel;
            // The host took the file's model: the thread's own pick is put back, as its selector shows it.
            if (_model is not null)
                await _link.InvokeWithParameterObjectAsync("models/useForSession", new { model = _model }, CancellationToken.None);
        }
        if (reloaded.Taken.Contains("agentModel")) _agentRoleModel = reloaded.AgentModel.Trim();
        if (reloaded.ServerChanged) Say(Strings.AcpServerChangedNewThread + "\n\n");

        // Said once while it lasts: a settings file broken by a hand edit is followed by nothing, and the user who just
        // edited it would otherwise see their change do nothing, without a word.
        if (reloaded.Unreadable && !_settingsUnreadableSaid) Say(Strings.AcpSettingsUnreadable + "\n\n");
        _settingsUnreadableSaid = reloaded.Unreadable;
    }

    private bool _settingsUnreadableSaid;

    /// <summary>
    /// The agreements asked before the client knew this thread (<see cref="ApprovalRequest"/>), asked again now, inside
    /// the turn: the MCP servers start again (<c>mcp/retry</c>), and a repository's server asks for its agreement.
    /// </summary>
    private async Task AskAgainWhatWasAskedTooEarlyAsync()
    {
        if (Interlocked.Exchange(ref _askAgain, 0) == 0) return;
        try { await _link.InvokeWithCancellationAsync<JsonElement>("mcp/retry", cancellationToken: CancellationToken.None); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Diagnostics.Swallow("AcpSession.AskAgain", ex); }
    }

    /// <summary>
    /// At a turn's start, inside it: the selector is told when the model it shows is no longer the one that answers — the
    /// settings changed it, or the mode did (<c>session/set_mode</c> carries no options back).
    /// </summary>
    private void AnnounceModelIfChanged()
    {
        if (string.Equals(_announcedModel, CurrentModel, StringComparison.Ordinal)
            && _announcedModels.SequenceEqual(_models, StringComparer.Ordinal)) return;
        Update(new { sessionUpdate = "config_option_update", configOptions = ConfigOptions() });
    }

    /// <summary>The installed models the client's selector last listed.</summary>
    private List<string> _announcedModels = [];

    /// <summary>
    /// At a turn's start, the installed models read again — and the default model adopted, when the server could not be
    /// asked as the thread opened.
    /// </summary>
    /// <remarks>⚠ Read once, at the thread's start: a server started after the editor (the ordinary order), or a model
    /// pulled since, left the selector offering the default alone for the thread's whole life, a model picked "unknown".
    /// VS Code reads them again when the server comes back.</remarks>
    private async Task ReadModelsAgainAsync()
    {
        var hadNone = _models.Count == 0;
        await RefreshModelsAsync(CancellationToken.None);
        if (hadNone && _models.Count > 0) await AdoptDefaultModelAsync(CancellationToken.None);
    }

    private bool CancelRequested { get { lock (_gate) return _cancelRequested; } }

    /// <summary><c>session/cancel</c>: the host stops the turn; the prompt then answers <c>cancelled</c>.</summary>
    /// <remarks>⚠ The turn's own request is never cancelled here: abandoned, it would leave the host still streaming
    /// while the prompt has already answered — updates after the response, which the protocol forbids. The host is
    /// told, and the turn ends when it does.</remarks>
    public async Task CancelAsync()
    {
        lock (_gate)
        {
            if (!_turnOpen) return;
            _cancelRequested = true;
        }
        try { await _link.InvokeWithCancellationAsync("chat/cancel"); }
        catch (Exception ex) { Diagnostics.Swallow("AcpSession.Cancel", ex); }
    }

    /// <remarks>⚠ A command is what the user typed, whatever the client attached beside it: Qt Creator attaches the
    /// current file to a new chat's first message, and a command that also asks for no attachment never reaches the host
    /// there — it goes to the model as a question. ⚠ And what the client attached goes with whatever the command becomes:
    /// a prompt it expands to, or the question it is when the host leaves it.</remarks>
    private async Task<string> RunTurnAsync(AcpPromptContent content)
    {
        var typed = content.Text.Trim();
        if (SlashCommandRouter.LooksLikeCommand(typed)) return await SlashAsync(typed, content);
        return await ChatAsync(typed, content);
    }

    private async Task<string> SlashAsync(string typed, AcpPromptContent content)
    {
        var head = typed.Split([' ', '\t', '\n'], 2)[0];
        if (EditorOnlyCommands.Contains(head)) return SayNotice(typed, Strings.AcpCommandNeedsEditor(head.ToLowerInvariant()));
        if (IsClear(head)) return SayNotice(typed, Strings.AcpClearStartNewThread);
        // ⚠ A thread's /model is the thread's, as its selector is: through the host it saved the default model of every
        // editor — Visual Studio, VS Code and the other threads changed model because one thread asked.
        if (head.Equals("/model", StringComparison.OrdinalIgnoreCase) && typed.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries) is [_, var picked, ..])
            return await PickModelAsync(typed, picked);
        if (StoppedBeforeTheHost(typed)) return "cancelled";

        SlashCommandResult result;
        try
        {
            result = await _link.InvokeWithParameterObjectAsync<SlashCommandResult>(
                "command/slash", new { text = typed, promptHistory = _prompts }, CancellationToken.None);
        }
        catch (RemoteInvocationException ex)
        {
            // The host refused (a turn already running): its sentence, never the command sent to the model.
            return SayNotice(typed, ex.Message);
        }
        if (!result.Handled) return await ChatAsync(typed, content);

        var notes = new List<string>();
        string? sendAsPrompt = null;
        foreach (var e in result.Effects ?? [])
        {
            switch (e.Kind)
            {
                case "sendAsPrompt":     sendAsPrompt = e.Value; break;
                case "setPrompt":        if (!string.IsNullOrEmpty(e.Value)) notes.Add(CopyBlock(Strings.AcpPasteThis, e.Value)); break;
                case "copyToClipboard":  if (!string.IsNullOrEmpty(e.Value)) notes.Add(CopyBlock(Strings.AcpCopyThis, e.Value)); break;
                case "openFile":         if (!string.IsNullOrEmpty(e.Value)) notes.Add(Strings.AcpOpenThis(e.Value)); break;
                case "attachChip":
                    if (!string.IsNullOrEmpty(e.Value))
                    {
                        var name = string.IsNullOrWhiteSpace(e.Name) ? "attachment" : e.Name;
                        lock (_gate) _pending.Add((name, e.Value));
                        notes.Add(Strings.AcpAttachedNext(name));
                    }
                    break;
                case "stateChange" when e.Name == "planMode":
                    ModeChanged(e.Value == "on" ? AcpModes.Plan : AcpModes.Default);
                    break;
                case "stateChange" when e.Name == "model" && !string.IsNullOrEmpty(e.Value):
                    _model = e.Value;
                    Update(new { sessionUpdate = "config_option_update", configOptions = ConfigOptions() });
                    break;
                case "configSaved":
                    // A command that saved the settings may have set the default or the agent's model (/onboard apply,
                    // /import): the selector names the model that answers, and config/reload never reports what the
                    // thread wrote itself.
                    await RefreshModelsAsync(CancellationToken.None);
                    await ReadConfiguredModelsAsync(CancellationToken.None);
                    Update(new { sessionUpdate = "config_option_update", configOptions = ConfigOptions() });
                    break;
                case "openSettings":
                    notes.Add(await _agent.OpenSettingsWindowAsync(Cwd, CancellationToken.None));
                    break;
                case "clearTranscript":
                    await ArchiveAndStartOverAsync();
                    break;
                case "exportRequest":
                    // No file to save it into: the conversation as this thread showed it, rendered by the host as the
                    // editors' export is, to copy.
                    notes.Add(await ExportTextAsync());
                    break;
            }
        }
        if (sendAsPrompt is not null) return await ChatAsync(sendAsPrompt, content with { Text = sendAsPrompt });

        var said = string.Join("\n\n", new[] { result.Markdown ?? string.Empty }.Concat(notes).Where(s => s.Length > 0));
        return SayNotice(typed, said);
    }

    /// <summary>
    /// The host started a new conversation under this thread (a <c>/template</c>): what was said so far is kept under a
    /// name of its own, and this session's file starts again — written over, the earlier turns would be lost.
    /// </summary>
    private async Task ArchiveAndStartOverAsync()
    {
        List<SavedMessageDto> before;
        lock (_gate) { before = [.. _transcript]; _transcript.Clear(); }
        if (before.Count == 0) return;
        var name = $"{Id}-{DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture)}";
        try
        {
            await _link.InvokeWithParameterObjectAsync("session/save", new { name, messages = before, archive = true });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Swallow("AcpSession.Archive", ex);
            Say("\n\n" + Strings.SessionArchiveFailed(ex.Message));
        }
    }

    private async Task<string> ChatAsync(string typed, AcpPromptContent content)
    {
        var (promptText, labels, attachedPaths) = await BuildPromptAsync(typed, content);
        // ⚠ A Stop that comes while the prompt is being built (attachments read, the host asked for each excerpt, a slash
        // command answered) finds no turn on the host to stop: chat/cancel does nothing there, and sent now, the whole
        // turn would run after it — tools and writes included — under a response that says "cancelled".
        if (StoppedBeforeTheHost(ChatTurnPolicy.BuildBubbleText(typed, labels))) return "cancelled";
        lock (_gate)
        {
            _prompts.Add(typed);
            _transcript.Add(new SavedMessageDto("user", ChatTurnPolicy.BuildBubbleText(typed, labels), Timestamp: Now()));
        }

        _provisional = _mode == AcpModes.Agent;
        _actBreak = false;
        // ⚠ Never cancelled by the token: see CancelAsync.
        var result = await _link.InvokeWithParameterObjectAsync<ChatSendResult>("chat/send", new
        {
            prompt        = promptText,
            query         = typed,
            model         = _model,
            // A model picked in this thread answers whatever the mode: it is what the selector shows.
            modelOverridesRoles = _model is not null,
            agentMode     = _mode == AcpModes.Agent,
            attachedPaths = attachedPaths.Count > 0 ? attachedPaths : null,
        }, CancellationToken.None);
        await DrainLinkAsync();
        Interlocked.Add(ref _sessionTokens, result.TokensUsed);

        if (result.Error is { } error)
        {
            // A failed turn is an error, never an answer: the client shows the response's message. What the client already
            // showed of the answer stays, as in both editors — a notice, never what the model said (a reload hands it no
            // half reply); left out, the reloaded thread lost what the user had read.
            var shown = _provisional ? string.Empty : _stream.Sent.Trim();
            if (shown.Length > 0) RecordAnswer(shown, notice: true);
            RecordAnswer(error, notice: true);
            await SaveAsync();
            throw AcpRpc.Error(AcpRpc.InternalError, error);
        }

        // The answer the host settled on, when the stream did not already show it: a run that only called tools, an
        // answer that came whole, the sentence for an empty reply — and always, after the agent's plan (_provisional).
        var answer = _provisional ? string.Empty : _stream.Sent.Trim();
        var final = MarkdownParser.ShownText("assistant", result.Text).Trim();
        if (final.Length > 0 && !answer.Contains(final, StringComparison.Ordinal))
        {
            Say((answer.Length > 0 ? "\n\n" : string.Empty) + final);
            answer = answer.Length > 0 ? answer + "\n\n" + final : final;
        }
        if (answer.Length > 0) RecordAnswer(answer, notice: false);
        if (result.Cancelled)
        {
            // As in both editors: the partial answer stays, and the stop is a line of its own — saved bare, a reloaded
            // session read the cut answer as complete. Stopped before the question entered the host's history, the
            // question is a notice: reloaded, it would reach the model as asked when the host never saw it.
            if (!result.QuestionKept) MarkLastQuestionNotice();
            RecordAnswer(Strings.MsgCancelled, notice: true);
        }
        if (!string.IsNullOrEmpty(result.EndNotice))
        {
            Say("\n\n" + result.EndNotice);
            RecordAnswer(result.EndNotice, notice: true);
        }
        // The run the turn made goes with its last message, as in both editors: going back to a question finds it there.
        if ((result.Run is { RunId.Length: > 0 } run ? run.RunId : result.RunId) is { Length: > 0 } runId)
            lock (_gate)
                if (_transcript.Count > 0 && _transcript[^1].Run is null) _transcript[^1] = _transcript[^1] with { Run = runId };
        if (!result.Cancelled && _agent.TakeLimitsNotice() is { } limits) Notice("info", limits, "\n\n---\n" + limits);
        if (result.ContextWindow > 0)
            Update(new { sessionUpdate = "usage_update", used = Math.Max(0, result.NextTurnTokens), size = result.ContextWindow });
        await SaveAsync();

        return result.Cancelled ? "cancelled"
             : result.Ended == "iterationLimit" ? "max_turn_requests"
             : result.Ended == "cut" ? "max_tokens"
             : "end_turn";
    }

    /// <summary>
    /// The prompt the host is sent: the message, what Inferpal could not read of it, and each attached file through the
    /// host's excerpt (sized for the window of the model that answers, its cut said in the label).
    /// </summary>
    private async Task<(string Prompt, List<string> Labels, List<string> AttachedPaths)> BuildPromptAsync(
        string typed, AcpPromptContent content)
    {
        var prompt = new StringBuilder(typed);
        foreach (var note in content.Notes) prompt.Append("\n\n").Append(note);

        List<(string Name, string? Path, string? Text)> files;
        lock (_gate)
        {
            files = [.. _pending.Select(p => (p.Name, (string?)null, (string?)p.Content)),
                     .. content.Attachments.Select(a => (a.Name, a.Path, a.Text))];
            _pending.Clear();
        }

        var labels = new List<string>();
        var attachedPaths = new List<string>();
        var left = new List<string>();
        var taken = 0;
        foreach (var (name, path, given) in files)
        {
            if (taken == MaxAttachedFiles) { left.Add(name); continue; }
            // Excluded by the workspace's ignore files — even when the client sent the text: named to both readers.
            if (path is not null && Services.Governance.ContentExclusions.Of(path) is { } why)
            {
                var shown = Relative(path);
                prompt.Append("\n\n### ").Append(shown).Append('\n').Append(why.ForModel(shown));
                labels.Add(Strings.ExcludedFromAi(shown, why.File, why.Line, why.Pattern));
                continue;
            }
            string body;
            try { body = given ?? await TextFileEncoding.ReadTextAsync(path!, CancellationToken.None); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                labels.Add(Strings.AcpAttachmentUnreadable(name, ex.Message));
                continue;
            }
            taken++;
            var shownName = path is null ? name : Relative(path);
            var excerpt = await _link.InvokeWithParameterObjectAsync<CodeExcerptResult>("code/excerpt", new
            {
                code = body, fileName = shownName, selection = false, model = _model,
                // Sized for the model that will read it, as chat/send resolves it for this turn.
                codeAction = false, agentMode = _mode == AcpModes.Agent, modelOverridesRoles = _model is not null,
            }, CancellationToken.None);
            prompt.Append("\n\n### ").Append(shownName).Append("\n```\n").Append(excerpt.Text).Append("\n```");
            labels.Add(excerpt.Label);
            // A file attached IN PART is not attached: the auto-context still offers its other chunks.
            if (path is not null && !excerpt.Truncated) attachedPaths.Add(path);
        }
        if (left.Count > 0) labels.Add(Strings.AcpAttachmentsLeftOut(left.Count, MaxAttachedFiles, string.Join(", ", left)));
        return (prompt.ToString(), labels, attachedPaths);
    }

    private string Relative(string path)
    {
        try
        {
            var rel = Path.GetRelativePath(Cwd, path);
            return rel.StartsWith("..", StringComparison.Ordinal) ? path : rel.Replace('\\', '/');
        }
        catch (ArgumentException) { return path; }
    }

    /// <summary>A command answered without the model: shown, and kept as notices — never as a question and its answer.</summary>
    private string SayNotice(string typed, string text)
    {
        Say(text);
        lock (_gate)
        {
            _transcript.Add(new SavedMessageDto("user", typed, SessionManager.NoticeMarker, Now()));
            _transcript.Add(new SavedMessageDto("assistant", text, SessionManager.NoticeMarker, Now()));
        }
        _ = SaveAsync();
        return "end_turn";
    }

    private void RecordAnswer(string text, bool notice)
    {
        lock (_gate)
            _transcript.Add(new SavedMessageDto("assistant", text, notice ? SessionManager.NoticeMarker : null, Now()));
    }

    /// <summary>The last question, kept on screen but out of what a reload gives the model (it never reached the host).</summary>
    private void MarkLastQuestionNotice()
    {
        lock (_gate)
        {
            var i = _transcript.FindLastIndex(m => m.Role == "user");
            if (i >= 0) _transcript[i] = _transcript[i] with { ToolName = SessionManager.NoticeMarker };
        }
    }

    /// <summary>
    /// A Stop that came before the host had a turn to stop: nothing is sent, the question is kept as a notice (the host
    /// never saw it), and the turn answers <c>cancelled</c>.
    /// </summary>
    private bool StoppedBeforeTheHost(string shown)
    {
        if (!CancelRequested) return false;
        lock (_gate) _transcript.Add(new SavedMessageDto("user", shown, SessionManager.NoticeMarker, Now()));
        _ = SaveAsync();
        return true;
    }

    /// <summary>One save at a time (see <see cref="SaveAsync"/>).</summary>
    private readonly SemaphoreSlim _saving = new(1, 1);

    /// <summary><c>/export</c>: the conversation as this thread showed it, rendered by the host's <c>chat/export</c> —
    /// the document the editors save — in a block to copy, the client having no file to save it into.</summary>
    private async Task<string> ExportTextAsync()
    {
        List<SavedMessageDto> shown;
        lock (_gate) shown = [.. _transcript];
        var messages = new List<ChatExportMessage>();
        for (var i = 0; i < shown.Count; i++)
        {
            var m = shown[i];
            var notice = m.ToolName == SessionManager.NoticeMarker;
            // An earlier /export and its answer, which holds the whole conversation: exported again, every export would
            // carry the one before it. SayNotice records the two together.
            if (notice && m.Role == "user" && m.Content.TrimStart().StartsWith("/export", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < shown.Count && shown[i + 1] is { Role: "assistant" } next && next.ToolName == SessionManager.NoticeMarker) i++;
                continue;
            }
            // A tool turn carries its title where a reply carries the notice marker.
            messages.Add(new ChatExportMessage(m.Role, m.Role == "tool" ? m.ToolName : null, m.Content, m.Timestamp, notice));
        }
        if (!messages.Any(m => !m.Notice)) return Strings.ExportNoMessages;

        string document;
        try
        {
            document = await _link.InvokeWithParameterObjectAsync<string>(
                "chat/export", new ChatExportParams(AsPlainText: false, messages, Volatile.Read(ref _sessionTokens), Model: CurrentModel),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is RemoteInvocationException or ConnectionLostException or ObjectDisposedException)
        {
            return Strings.ExportFailed(ex.Message);
        }
        return CopyBlock(Strings.AcpCopyThis, document, "markdown");
    }

    /// <summary>Text the user takes by hand, under the sentence that says what to do with it: in a fence longer than any
    /// run of backticks inside, so a code block in the text never closes it early.</summary>
    private static string CopyBlock(string lead, string text, string language = "") =>
        $"{lead}\n\n{MarkdownParser.Fence(text, language)}";

    /// <summary>The session, saved under its id — what <c>session/list</c> and <c>session/load</c> find again — as the
    /// transcript holds it now; a failure is said in the thread.</summary>
    /// <remarks>
    /// ⚠ One save at a time, the transcript read INSIDE: two saves in flight — a turn's end, then a <c>/task</c> report a
    /// moment later — reach the host in parallel, and the one that read the older transcript could finish last: the
    /// report gone from the saved thread, nothing saved after it.
    /// </remarks>
    private async Task SaveAsync()
    {
        await _saving.WaitAsync();
        try
        {
            List<SavedMessageDto> messages;
            lock (_gate) messages = [.. _transcript];
            if (messages.Count == 0) return;
            try
            {
                await _link.InvokeWithParameterObjectAsync("session/save", new { name = Id, messages });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Diagnostics.Swallow("AcpSession.Save", ex);
                Say("\n\n" + Strings.AcpSessionNotSaved(ex.Message));
            }
        }
        finally { _saving.Release(); }
    }

    private static string Now() => DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture);

    // ── Replay ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads the saved session into the host and, unless <paramref name="replay"/> is false, replays it to the client —
    /// before <c>session/load</c> answers. <c>false</c> when there is no such session.
    /// </summary>
    public async Task<bool> LoadAsync(bool replay, CancellationToken ct)
    {
        var loaded = await _link.InvokeWithParameterObjectAsync<SessionLoadResult?>("session/load", new { name = Id }, ct);
        if (loaded is null) return false;
        // What the host said while loading (a held mode whose file is gone) reaches the client before the load answers.
        await DrainLinkAsync();
        // The mode the thread was held in, held again — and the client's selectors follow it. Gone, the host said so.
        if (loaded.Mode is { } held) await HoldAgainAsync(AcpModes.FoundPrefix + held, ct);
        else if (AcpModes.IsFound(_mode)) _mode = AcpModes.Default;
        lock (_gate) { _transcript.Clear(); _transcript.AddRange(loaded.Messages); }
        if (replay) Replay(loaded.Messages);
        await _agent.FlushAsync();
        return true;
    }

    /// <summary>The conversation as the client draws it: questions, answers, and each tool line as a finished call.</summary>
    internal void Replay(IEnumerable<SavedMessageDto> messages)
    {
        // The client joins consecutive message chunks into one message: a notice saved after its answer is set apart
        // by a blank line, as the live turn sends it — glued, it read as the answer's last words.
        var afterAnswer = false;
        foreach (var m in messages)
        {
            var wasAfterAnswer = afterAnswer;
            afterAnswer = false;
            switch (m.Role)
            {
                case "user":
                    // A note typed during a run (an editor saved it) follows its question: set apart, never glued to it.
                    Update(new { sessionUpdate = "user_message_chunk",
                                 content = Text(m.ToolName == SessionManager.NoteMarker ? "\n\n" + m.Content : m.Content) });
                    break;
                case "tool":
                    var id = "replay_" + Interlocked.Increment(ref _replayed).ToString(CultureInfo.InvariantCulture);
                    // The saved name is the title drawn live ("read_file · alpha.txt"), or a bare tool name (VS, VS Code).
                    var title = m.ToolName is { Length: > 0 } t && t != SessionManager.NoticeMarker ? t : "tool";
                    Update(new
                    {
                        sessionUpdate = "tool_call", toolCallId = id, title, kind = AcpToolCalls.Kind(AcpToolCalls.ToolOf(title)),
                        status = "completed", content = new[] { TextContent(AcpToolCalls.Block(m.Content, 4_000)) },
                    });
                    break;
                default:
                    var shown = MarkdownParser.ShownText("assistant", m.Content);
                    if (shown.Length > 0)
                    {
                        Update(new { sessionUpdate = "agent_message_chunk", content = Text(wasAfterAnswer ? "\n\n" + shown : shown) });
                        afterAnswer = true;
                    }
                    else afterAnswer = wasAfterAnswer;
                    break;
            }
        }
    }

    /// <summary>The live conversation, for a <c>session/load</c> of a session this process already holds.</summary>
    public void ReplayLive()
    {
        List<SavedMessageDto> messages;
        lock (_gate) messages = [.. _transcript];
        Replay(messages);
    }

    // ── To the client ──────────────────────────────────────────────────────────

    private void Update(object update) => _agent.Notify("session/update", new { sessionId = Id, update });

    private static object Text(string text) => new { type = "text", text };

    private static object TextContent(string text) => new { type = "content", content = Text(text) };

    /// <summary>Text said in this turn beside the model's stream (a notice, the answer the stream did not carry).</summary>
    private void Say(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Update(new { sessionUpdate = "agent_message_chunk", content = Text(text) });
    }

    /// <summary>
    /// Something Inferpal says beside the answer — a model it adopted, what this agent does not do, a rule written: a
    /// session notice when the client declared it shows them (ACP 1.11), shown outside the transcript; the text
    /// <paramref name="fallback"/> otherwise, as before.
    /// </summary>
    /// <remarks>
    /// ⚠ Never what the person needs to finish a task — the end of a cut answer, a command's answer, a <c>/task</c>'s
    /// report: a notice is not replayed, and a client may let it be dismissed.
    /// </remarks>
    /// <returns>True when it went as a notice.</returns>
    private bool Notice(string severity, string text, string fallback)
    {
        if (!_agent.ClientShowsNotices || NoticeParts(text) is not var (title, description))
        {
            Say(fallback);
            return false;
        }
        Update(new { sessionUpdate = "notice", severity, title, description });
        return true;
    }

    /// <summary>A notice's plain-text title — the first sentence of <paramref name="text"/> — and the rest as its description;
    /// <c>null</c> when there is no text.</summary>
    /// <remarks>Title and description are plain text by the protocol, and the title short to be read: a whole paragraph
    /// of Markdown drawn as one heading reads as noise. Code marks and emphasis are dropped, a link keeps its text and its
    /// address.</remarks>
    internal static (string Title, string? Description)? NoticeParts(string text)
    {
        var budget = RegexBudget.Default;
        var plain = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]+)\]\(([^)\s]+)\)", "$1 ($2)",
                        System.Text.RegularExpressions.RegexOptions.None, budget);
        plain = System.Text.RegularExpressions.Regex.Replace(plain, @"\*\*(.+?)\*\*|__(.+?)__", "$1$2",
                    System.Text.RegularExpressions.RegexOptions.None, budget);
        plain = plain.Replace("`", string.Empty, StringComparison.Ordinal).Trim();
        if (plain.Length == 0) return null;
        var lineEnd = plain.IndexOf('\n');
        var firstLine = lineEnd < 0 ? plain : plain[..lineEnd].TrimEnd();
        var sentence = System.Text.RegularExpressions.Regex.Match(firstLine, @"^.+?[.!?](?=\s|$)",
            System.Text.RegularExpressions.RegexOptions.None, RegexBudget.Default);
        var title = sentence.Success ? sentence.Value : firstLine;
        var rest = plain[title.Length..].Trim();
        return (title, rest.Length > 0 ? rest : null);
    }

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>Waits until every notification the host has sent so far has been handled.</summary>
    private Task DrainLinkAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _linkContext.Post(_ => done.TrySetResult(), null);
        return done.Task;
    }

    // ── From the host (the adapter's half of the host protocol) ─────────────────

    private bool InTurn { get { lock (_gate) return _turnOpen; } }

    [JsonRpcMethod("chat/token", UseSingleObjectParameterDeserialization = true)]
    public void ChatToken(TextNote note)
    {
        if (!InTurn) return;
        var piece = _stream.Append(note.Text ?? string.Empty);
        if (piece.Length == 0) return;
        if (!_provisional)
        {
            Update(new { sessionUpdate = "agent_message_chunk", content = Text(piece) });
            return;
        }
        if (_actBreak)
        {
            piece = "\n\n" + piece.TrimStart();
            _actBreak = false;
        }
        Update(new { sessionUpdate = "agent_thought_chunk", content = Text(piece) });
    }

    [JsonRpcMethod("chat/reasoning", UseSingleObjectParameterDeserialization = true)]
    public void ChatReasoning(TextNote note)
    {
        if (!InTurn || string.IsNullOrEmpty(note.Text)) return;
        Update(new { sessionUpdate = "agent_thought_chunk", content = Text(note.Text) });
    }

    // Progress the VS Code panel draws in its own way: ACP has no wire for a status line, and the reasoning preview and
    // the step pause are covered by chat/reasoning and by /agent-step not being offered.
    [JsonRpcMethod("chat/thinking")]     public void ChatThinking() { }
    [JsonRpcMethod("chat/step")]         public void ChatStep() { }
    /// <summary>
    /// The model's wait, as a thought: a client draws thoughts as they come and never updates them, so it is said once
    /// when it shows (what the server reads, then the detail with its bound) and once more when it runs longer than
    /// usual. Nothing that goes stale is written — no time, no "Stop ends it now" — since the line stays in the thread
    /// once the wait is over, while the client animates its own "working" and shows its own Stop.
    /// </summary>
    [JsonRpcMethod("chat/wait", UseSingleObjectParameterDeserialization = true)]
    public void ChatWait(WaitNote note)
    {
        var stage = note.Wait is null ? 0 : note.Wait.Longer ? 2 : 1;
        lock (_gate)
        {
            if (!_turnOpen || (stage != 0 && stage <= _waitSaid)) return;
            _waitSaid = stage;
        }
        if (note.Wait is { } wait)
            Update(new { sessionUpdate = "agent_thought_chunk", content = Text($"\n\n{wait.Title}\n\n{wait.DetailWithoutStop}\n\n") });
    }
    /// <summary>What the model wrote so far in this act is not the answer: the next act's thinking starts a paragraph.</summary>
    [JsonRpcMethod("chat/streamReset")]  public void ChatStreamReset() => _actBreak = _provisional;
    [JsonRpcMethod("chat/stepPaused")]   public void ChatStepPaused() { }
    [JsonRpcMethod("chat/stepResumed")]  public void ChatStepResumed() { }

    [JsonRpcMethod("chat/plan", UseSingleObjectParameterDeserialization = true)]
    public void ChatPlan(PlanNote note)
    {
        if (!InTurn) return;
        lock (_gate)
        {
            _plan.Clear();
            foreach (var step in note.Steps ?? []) _plan.Add(new PlanEntryState(step, "pending"));
        }
        SendPlan();
    }

    [JsonRpcMethod("chat/stepUpdate", UseSingleObjectParameterDeserialization = true)]
    public void ChatStepUpdate(StepUpdateNote note)
    {
        if (!InTurn) return;
        lock (_gate)
        {
            if (note.Index < 0 || note.Index >= _plan.Count) return;
            // ⚠ ACP has three states, and a client draws "completed" as done — struck through, the plan "All Done". A step
            // that failed or was skipped was NOT done: it stays pending, and its text says why.
            var (status, suffix) = note.Status switch
            {
                nameof(Models.AgentStepStatus.Active)  => ("in_progress", string.Empty),
                nameof(Models.AgentStepStatus.Done)    => ("completed", string.Empty),
                nameof(Models.AgentStepStatus.Failed)  => ("pending", " — " + Strings.AcpPlanStepFailed),
                nameof(Models.AgentStepStatus.Skipped) => ("pending", " — " + Strings.AcpPlanStepSkipped),
                _                                      => ("pending", string.Empty),
            };
            var entry = _plan[note.Index];
            var content = entry.Content.EndsWith(suffix, StringComparison.Ordinal) ? entry.Content : entry.Content + suffix;
            _plan[note.Index] = new PlanEntryState(content, status);
        }
        SendPlan();
    }

    private void SendPlan()
    {
        object[] entries;
        lock (_gate) entries = [.. _plan.Select(p => new { content = p.Content, priority = "medium", status = p.Status })];
        Update(new { sessionUpdate = "plan", entries });
    }

    [JsonRpcMethod("chat/toolStart", UseSingleObjectParameterDeserialization = true)]
    public void ChatToolStart(ToolStartNote note)
    {
        if (!InTurn) return;
        var raw = AcpToolCalls.RawInput(note.Input ?? "{}");
        var args = raw ?? EmptyObject;
        var title = AcpToolCalls.Title(note.Name, args, Cwd);
        _calls[note.CallId] = new ToolCallState { Name = note.Name, Title = title, Key = AcpToolCalls.CallKey(note.Input) };
        Update(new
        {
            sessionUpdate = "tool_call",
            toolCallId    = note.CallId,
            title         = title,
            kind          = AcpToolCalls.Kind(note.Name),
            status        = "in_progress",
            rawInput      = raw,
            locations     = Locations(args),
        });
    }

    private object[] Locations(JsonElement args) =>
        [.. Services.Execution.ToolRegistry.NamedFiles(args, Cwd).Distinct(PathComparer.Default).Take(10).Select(p => new { path = p })];

    [JsonRpcMethod("chat/toolEnd", UseSingleObjectParameterDeserialization = true)]
    public void ChatToolEnd(ToolEndNote note)
    {
        if (!InTurn) return;
        var state = _calls.TryGetValue(note.CallId, out var known) ? known : null;
        if (state is not null) lock (_gate) _ended.Add((state.Name, state.Key));
        var output = note.Output ?? string.Empty;
        var content = new List<object>();
        if (state is not null) content.AddRange(state.Diffs);
        if (output.Length > 0) content.Add(TextContent(AcpToolCalls.Block(output)));
        Update(new
        {
            sessionUpdate = "tool_call_update",
            toolCallId    = note.CallId,
            status        = state is { Rejected: true } ? "failed" : "completed",
            content,
        });
        if (state is not null)
            lock (_gate) _transcript.Add(new SavedMessageDto("tool", AcpToolCalls.Shown(output, 2_000), state.Title, Now()));
    }

    /// <summary>
    /// The loop's report of a call (<c>chat/tool</c>). A call announced by <c>chat/toolStart</c> is already drawn: its
    /// report is the echo. The others are reports the loop makes on its own — a call refused before it reached a tool, a
    /// compaction of the conversation, a session recap — and each becomes a finished call of its own.
    /// </summary>
    /// <remarks>⚠ The echo is matched on WHO the call is (tool and arguments), never counted: in a batch run in parallel,
    /// a call refused before any tool reported between two that ran — counted, the refusal was taken for an echo and
    /// vanished, and a call that ran was drawn a second time.</remarks>
    [JsonRpcMethod("chat/tool", UseSingleObjectParameterDeserialization = true)]
    public void ChatTool(ToolNotice note)
    {
        if (!InTurn) return;
        var key = AcpToolCalls.CallKey(note.Input);
        lock (_gate)
        {
            var echo = _ended.FindIndex(e => e.Name == note.Name && e.Key == key);
            if (echo >= 0)
            {
                _ended.RemoveAt(echo);
                return;
            }
        }
        var id = "note_" + Interlocked.Increment(ref _replayed).ToString(CultureInfo.InvariantCulture);
        Update(new
        {
            sessionUpdate = "tool_call",
            toolCallId    = id,
            title         = note.Name,
            kind          = AcpToolCalls.Kind(note.Name),
            status        = note.HasErrors ? "failed" : "completed",
            content       = new[] { TextContent(AcpToolCalls.Block(note.Output)) },
        });
    }

    /// <summary>
    /// A compaction before the question (<c>chat/compaction</c>): a <c>compaction_update</c> when the client declared it
    /// shows them (ACP 1.11) — completed, with the summary the model now reads when there is one, or failed when the
    /// summary could not be written and the turns were dropped instead — and otherwise the <c>context_compact</c> call the
    /// client was always drawn.
    /// </summary>
    /// <remarks>⚠ An identifier is never reused within the session — a thread evicted and reopened is a new object with
    /// the same session id, so it is never a counter. One update with the terminal status is the whole report. Turns lost
    /// or a summary cut are said apart, beside the compaction they qualify: a warning notice, or the transcript's text.</remarks>
    [JsonRpcMethod("chat/compaction", UseSingleObjectParameterDeserialization = true)]
    public void ChatCompaction(CompactionNote note)
    {
        if (!InTurn) return;
        if (!_agent.ClientShowsCompaction)
        {
            ChatTool(new ToolNotice("context_compact", string.Empty, note.Notice, note.Degraded));
            return;
        }
        var id = "cmp_" + Guid.NewGuid().ToString("N");
        if (note.Failed)
        {
            Update(new { sessionUpdate = "compaction_update", compactionId = id, status = "failed", error = note.Notice });
            return;
        }
        Update(new
        {
            sessionUpdate = "compaction_update", compactionId = id, status = "completed",
            summary = note.Summary is { Length: > 0 } s ? new[] { Text(s) } : null,
        });
        if (note.Degraded) Notice("warning", note.Notice, fallback: "\n\n" + note.Notice);
    }

    [JsonRpcMethod("host/notice", UseSingleObjectParameterDeserialization = true)]
    public void HostNotice(TextNote note) => Keep(note, asNotice: !note.Result);

    /// <summary>A sentence of the host, shown and kept with the thread: a notice when <paramref name="asNotice"/> and the
    /// client shows notices, the answer's text otherwise.</summary>
    private void Keep(TextNote note, bool asNotice)
    {
        if (string.IsNullOrEmpty(note.Text)) return;
        // A notice is live only: shown as one, it is not kept to come back as the answer's text when the thread reloads.
        if (asNotice && Notice(note.Severity ?? "info", note.Text, "\n\n" + note.Text)) return;
        if (!asNotice) Say("\n\n" + note.Text);
        // ⚠ Kept with the thread, as both editors keep it — a notice, out of what the model reads back. Shown only, a
        // /task's report (the only place its result is) was gone once the thread was reloaded, or reopened after an
        // eviction. Outside a turn nobody saves after it: saved here — once the user wrote in the thread, a command
        // included (a start's notice alone would make a session file of nothing); a turn saves at its end.
        RecordAnswer(note.Text, notice: true);
        bool written;
        lock (_gate) written = _transcript.Any(m => m.Role == "user");
        if (!InTurn && written) _ = SaveAsync();
    }

    /// <summary>A <c>/task</c>'s report: the only place its result is, so never a notice — a notice is not replayed, and
    /// carries nothing the person needs to finish a task (ACP 1.11).</summary>
    [JsonRpcMethod("task/finished", UseSingleObjectParameterDeserialization = true)]
    public void TaskFinished(TextNote note) => Keep(note, asNotice: false);

    /// <summary>
    /// The approval card, as <c>session/request_permission</c>: the call it belongs to with the files' real changes, and
    /// the three answers of the card. A cancelled request is a refusal: fail closed.
    /// </summary>
    [JsonRpcMethod("approval/request", UseSingleObjectParameterDeserialization = true)]
    public async Task<int> ApprovalRequest(ApprovalNote note)
    {
        // ⚠ Asked while the client does not know this session — a repository's MCP server, at session/new — the request
        // names a session the client never heard of: refused, and the server stayed off for the thread's whole life (its
        // next start is the next thread). Refused here, and asked again at the first prompt.
        if (!_known)
        {
            Interlocked.Exchange(ref _askAgain, 1);
            Diagnostics.Record("Acp", $"Session {Id}: an agreement ({note.Tool}) was asked before the client knew the session; asked again at its first prompt.");
            return 0;
        }
        var id = note.CallId ?? "approval_" + Interlocked.Increment(ref _approvals).ToString(CultureInfo.InvariantCulture);
        var diffs = (note.Diffs ?? [])
            .Where(d => !string.IsNullOrWhiteSpace(d.Path))
            .Select(d => (object)new { type = "diff", path = d.Path, oldText = string.IsNullOrEmpty(d.OldText) ? null : d.OldText, newText = d.NewText ?? string.Empty })
            .ToList();
        if (note.CallId is not null && _calls.TryGetValue(note.CallId, out var state)) state.Diffs = diffs;

        var title = note.Card is { Title: { Length: > 0 } t }
            ? t + (string.IsNullOrWhiteSpace(note.Card.Subject) ? string.Empty : " · " + note.Card.Subject)
            : note.Message.Split('\n', 2)[0];
        // The changes themselves when there are some (the client draws them), else the question as the card words it —
        // the command, the URL, the summary of a rename: what the human reads before answering.
        var content = diffs.Count > 0 ? diffs : [TextContent(note.Message)];
        // ⚠ The card the client already draws for the call gets the diffs first (same queue: on screen before the
        // question). Qt Creator keeps that card and ignores what the request carries: without this update the user
        // approves a change the card does not show. Diffs only — a question written on the card would stay there if the
        // turn ended before the call does.
        if (diffs.Count > 0 && note.CallId is not null && _calls.ContainsKey(note.CallId))
            Update(new { sessionUpdate = "tool_call_update", toolCallId = id, content = diffs });

        try
        {
            var answer = await _agent.RequestAsync<PermissionAnswer>("session/request_permission", new
            {
                sessionId = Id,
                toolCall  = new
                {
                    toolCallId = id,
                    title,
                    kind       = AcpToolCalls.Kind(note.Tool ?? string.Empty),
                    status     = "pending",
                    content,
                },
                options = Options(note.Card?.RuleLabel),
            });
            var decision = answer?.Outcome is { Outcome: "selected" } o ? o.OptionId switch
            {
                "allow_once"   => 1,
                "allow_always" => 2,
                "allow_rule"   => string.IsNullOrEmpty(note.Card?.RuleLabel) ? 0 : 3,
                _              => 0,
            } : 0;
            if (decision == 0 && note.CallId is not null && _calls.TryGetValue(note.CallId, out var refused)) refused.Rejected = true;
            return decision;
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("AcpSession.Permission", ex);
            if (note.CallId is not null && _calls.TryGetValue(note.CallId, out var failed)) failed.Rejected = true;
            return 0;
        }
    }

    /// <summary>
    /// The answers a permission request offers: the narrow rule the card describes, when it describes one, beside
    /// "always" — written to the machine's rules, where "always" lasts the session.
    /// </summary>
    private static object[] Options(string? ruleLabel)
    {
        var options = new List<object>
        {
            new { optionId = "allow_once",   name = Strings.ApprovalAllowOnce,   kind = "allow_once" },
            new { optionId = "allow_always", name = Strings.ApprovalAlwaysAllow, kind = "allow_always" },
        };
        if (!string.IsNullOrEmpty(ruleLabel))
            options.Add(new { optionId = "allow_rule", name = ruleLabel, kind = "allow_always" });
        options.Add(new { optionId = "reject_once", name = Strings.ApprovalDeny, kind = "reject_once" });
        return [.. options];
    }

    /// <summary>
    /// What the editor holds for <paramref name="note"/>'s path — its buffer, unsaved changes included — when the client
    /// reads files for its agents; <c>null</c> otherwise, never a request it did not offer to answer.
    /// </summary>
    [JsonRpcMethod("editor/buffer", UseSingleObjectParameterDeserialization = true)]
    public async Task<string?> EditorBuffer(PathNote note, CancellationToken ct)
    {
        if (!_fsRead || string.IsNullOrWhiteSpace(note.Path)) return null;
        try
        {
            var read = await _agent.RequestAsync<ReadAnswer>("fs/read_text_file", new { sessionId = Id, path = note.Path }, ct);
            return read?.Content;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Swallow("AcpSession.ReadBuffer", ex);
            return null;
        }
    }

    // No editor here (editorSurface: false): asked anyway, the answer is "none".
    [JsonRpcMethod("editor/activeDocument")]    public object? EditorActiveDocument() => null;
    [JsonRpcMethod("editor/diagnostics")]       public string? EditorDiagnostics() => null;
    [JsonRpcMethod("editor/insertAtCursor")]    public string? EditorInsert() => null;
    [JsonRpcMethod("editor/replaceSelection")]  public object? EditorReplace() => null;

    /// <summary>A value a repository's MCP server asks for: there is no input box to ask it in, so none is given.</summary>
    [JsonRpcMethod("input/request")]            public string? InputRequest() => null;

    // ── Wire shapes of the host's notifications ────────────────────────────────

    /// <summary>A sentence for the person; <paramref name="Severity"/> (<c>info</c>, <c>warning</c>) is set where it is said, and
    /// <paramref name="Result"/> marks the outcome of something the person asked for, which stays the thread's text.</summary>
    public sealed record TextNote(string? Text, string? Severity = null, bool Result = false);
    public sealed record PlanNote(string? Goal, List<string>? Steps);
    public sealed record StepUpdateNote(int Index, string? Status);
    public sealed record ToolStartNote(string CallId, string Name, string? Input);
    public sealed record ToolEndNote(string CallId, string? Output);
    public sealed record WaitNote(ModelWaitDto? Wait);
    public sealed record PathNote(string? Path);
    public sealed record CardNote(string? Title, string? Subject, string? RuleLabel = null);
    public sealed record DiffNote(string? Path, string? OldText, string? NewText);
    public sealed record ApprovalNote(string Message, CardNote? Card, string? CallId, string? Tool, string? Details, List<DiffNote>? Diffs);
    public sealed record PermissionOutcome(string? Outcome, string? OptionId);
    public sealed record PermissionAnswer(PermissionOutcome? Outcome);
    public sealed record ReadAnswer(string? Content);

    public async ValueTask DisposeAsync()
    {
        try { _link.Dispose(); } catch (Exception ex) { Diagnostics.Swallow("AcpSession.Dispose", ex); }
        try { _hostSide.Dispose(); } catch (Exception ex) { Diagnostics.Swallow("AcpSession.Dispose", ex); }
        // The host's own Dispose takes its MCP servers, shells and background tasks with it.
        await Task.Run(_host.Dispose);
    }
}
