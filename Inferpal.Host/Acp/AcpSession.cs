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
    /// <summary>The commands VS Code serves itself, on its editor or its panels: nothing here can serve them.</summary>
    internal static readonly HashSet<string> EditorOnlyCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "/xray", "/explain", "/review", "/fix", "/refactor", "/doc", "/export", "/branch", "/agent-step", "/resume",
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
        public List<object> Diffs = [];
        public bool Rejected;
    }

    private sealed record PlanEntryState(string Content, string Status);

    private string _mode = AcpModes.Default;
    private string? _model;
    private string _defaultModel = string.Empty;
    private List<string> _models = [];
    private AcpShownStream _stream = new();
    private bool _turnOpen;
    private bool _cancelRequested;
    private int _echoes;
    private int _approvals;
    private int _replayed;

    private AcpSession(AcpAgent agent, string id, string cwd, HostServer host, JsonRpc hostSide, JsonRpc link, bool fsRead)
    {
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
    public static async Task<AcpSession> StartAsync(AcpAgent agent, string id, string cwd,
                                                    IReadOnlyList<McpSessionServerDto> mcpServers, CancellationToken ct)
    {
        var (hostStream, linkStream) = FullDuplexStream.CreatePair();
        var host     = agent.CreateHost();
        var hostSide = HostRpc.Create(hostStream, hostStream, host);
        host.Attach(hostSide);
        hostSide.StartListening();

        var link    = HostRpc.Create(linkStream, linkStream);
        var session = new AcpSession(agent, id, cwd, host, hostSide, link, agent.ClientReadsFiles);
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
            }, ct);
            session._defaultModel = init.DefaultModel;
            await session.AdoptDefaultModelAsync(ct);
            if (mcpServers.Count > 0)
                await link.InvokeWithParameterObjectAsync("mcp/sessionServers", new { servers = mcpServers }, ct);
            session._mode = await session.ConfiguredModeAsync(ct);
            await session.RefreshModelsAsync(ct);
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

    /// <summary>The mode a new session opens in: the agent when the user turned agent mode on, else the default.</summary>
    private async Task<string> ConfiguredModeAsync(CancellationToken ct)
    {
        try
        {
            var json = await _link.InvokeWithCancellationAsync<string>("config/get", cancellationToken: ct);
            using var doc = JsonDocument.Parse(json);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (string.Equals(p.Name, "agentModeEnabled", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.True)
                    return AcpModes.Agent;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Diagnostics.Swallow("AcpSession.ConfiguredMode", ex); }
        return AcpModes.Default;
    }

    /// <summary>The installed chat models (an embedding model answers no question), read again on demand.</summary>
    private async Task RefreshModelsAsync(CancellationToken ct)
    {
        try
        {
            var listed = await _link.InvokeWithParameterObjectAsync<List<string>>("models/list", new { }, ct);
            _models = [.. (listed ?? []).Where(m => !ModelCatalog.IsEmbeddingModel(m)).Distinct(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The server may be down: the model the settings name is still offered, and the turn will say why it fails.
            Diagnostics.Swallow("AcpSession.Models", ex);
            _models = [];
        }
    }

    // ── Modes and options ───────────────────────────────────────────────────────

    /// <summary>The model the next turn asks: the one picked here, else the settings' default.</summary>
    private string CurrentModel => _model ?? _defaultModel;

    public object ModeState() => AcpModes.State(_mode);

    public List<object> ConfigOptions() => AcpModes.ConfigOptions(_mode, CurrentModel, _models);

    public async Task SetModeAsync(string modeId, CancellationToken ct)
    {
        if (!AcpModes.Ids.Contains(modeId)) throw AcpRpc.Error(AcpRpc.InvalidParams, $"Unknown mode '{modeId}'.");
        await _link.InvokeWithParameterObjectAsync<bool>("plan/mode", new { enabled = modeId == AcpModes.Plan }, ct);
        _mode = modeId;
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
    public async Task<string> PromptAsync(JsonElement prompt)
    {
        var content = AcpPromptBlocks.Read(prompt);
        lock (_gate)
        {
            if (_turnOpen) throw AcpRpc.Error(AcpRpc.InvalidParams, "A prompt is already running in this session.");
            _turnOpen = true;
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _cancelRequested = false;
            _stream = new AcpShownStream();
            _plan.Clear();
            _calls.Clear();
            _echoes = 0;
        }
        LastUsed = DateTime.UtcNow;
        try
        {
            await AnnounceCommandsOnceAsync(CancellationToken.None);
            if (Interlocked.Exchange(ref _startNotice, null) is { Length: > 0 } notice) Say(notice + "\n\n");
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

    private async Task<string> RunTurnAsync(AcpPromptContent content)
    {
        var typed = content.Text.Trim();
        if (typed.StartsWith('/') && content.Attachments.Count == 0 && content.Notes.Count == 0)
            return await SlashAsync(typed);
        return await ChatAsync(typed, content);
    }

    private async Task<string> SlashAsync(string typed)
    {
        var head = typed.Split([' ', '\t', '\n'], 2)[0];
        if (EditorOnlyCommands.Contains(head)) return SayNotice(typed, Strings.AcpCommandNeedsEditor(head.ToLowerInvariant()));
        if (IsClear(head)) return SayNotice(typed, Strings.AcpClearStartNewThread);

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
        if (!result.Handled) return await ChatAsync(typed, new AcpPromptContent(typed, [], []));

        var notes = new List<string>();
        string? sendAsPrompt = null;
        foreach (var e in result.Effects ?? [])
        {
            switch (e.Kind)
            {
                case "sendAsPrompt":     sendAsPrompt = e.Value; break;
                case "setPrompt":        if (!string.IsNullOrEmpty(e.Value)) notes.Add(Strings.AcpPasteThis + "\n\n```\n" + e.Value + "\n```"); break;
                case "copyToClipboard":  if (!string.IsNullOrEmpty(e.Value)) notes.Add(Strings.AcpCopyThis + "\n\n```\n" + e.Value + "\n```"); break;
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
                    await RefreshModelsAsync(CancellationToken.None);
                    Update(new { sessionUpdate = "config_option_update", configOptions = ConfigOptions() });
                    break;
                case "clearTranscript":
                    await ArchiveAndStartOverAsync();
                    break;
                case "exportRequest":
                    notes.Add(Strings.AcpCommandNeedsEditor("/export"));
                    break;
            }
        }
        if (sendAsPrompt is not null) return await ChatAsync(sendAsPrompt, new AcpPromptContent(sendAsPrompt, [], []));

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
        lock (_gate)
        {
            _prompts.Add(typed);
            _transcript.Add(new SavedMessageDto("user", ChatTurnPolicy.BuildBubbleText(typed, labels), Timestamp: Now()));
        }

        // ⚠ Never cancelled by the token: see CancelAsync.
        var result = await _link.InvokeWithParameterObjectAsync<ChatSendResult>("chat/send", new
        {
            prompt        = promptText,
            query         = typed,
            model         = _model,
            agentMode     = _mode == AcpModes.Agent,
            attachedPaths = attachedPaths.Count > 0 ? attachedPaths : null,
        }, CancellationToken.None);
        await DrainLinkAsync();

        if (result.Error is { } error)
        {
            // A failed turn is an error, never an answer: the client shows the response's message.
            RecordAnswer(error, notice: true);
            await SaveAsync();
            throw AcpRpc.Error(AcpRpc.InternalError, error);
        }

        // The answer the host settled on, when the stream did not already show it: a run that only called tools, an
        // answer that came whole, the sentence for an empty reply.
        var answer = _stream.Sent.Trim();
        var final = MarkdownParser.ShownText("assistant", result.Text).Trim();
        if (final.Length > 0 && !answer.Contains(final, StringComparison.Ordinal))
        {
            Say((answer.Length > 0 ? "\n\n" : string.Empty) + final);
            answer = answer.Length > 0 ? answer + "\n\n" + final : final;
        }
        if (answer.Length > 0) RecordAnswer(answer, notice: false);
        if (!string.IsNullOrEmpty(result.EndNotice))
        {
            Say("\n\n" + result.EndNotice);
            RecordAnswer(result.EndNotice, notice: true);
        }
        if (!result.Cancelled && _agent.TakeLimitsNotice() is { } limits) Say("\n\n---\n" + limits);
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

    /// <summary>The session, saved under its id — what <c>session/list</c> and <c>session/load</c> find again.</summary>
    private async Task SaveAsync()
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
                    Update(new { sessionUpdate = "user_message_chunk", content = Text(m.Content) });
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
        if (piece.Length > 0) Update(new { sessionUpdate = "agent_message_chunk", content = Text(piece) });
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
    [JsonRpcMethod("chat/streamReset")]  public void ChatStreamReset() { }
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
        _calls[note.CallId] = new ToolCallState { Name = note.Name, Title = title };
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
        Interlocked.Increment(ref _echoes);
        var state = _calls.TryGetValue(note.CallId, out var known) ? known : null;
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
    [JsonRpcMethod("chat/tool", UseSingleObjectParameterDeserialization = true)]
    public void ChatTool(ToolNotice note)
    {
        if (!InTurn) return;
        if (Interlocked.Decrement(ref _echoes) >= 0) return;
        Interlocked.Increment(ref _echoes);
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

    [JsonRpcMethod("host/notice", UseSingleObjectParameterDeserialization = true)]
    public void HostNotice(TextNote note)
    {
        if (string.IsNullOrEmpty(note.Text)) return;
        Update(new { sessionUpdate = "agent_message_chunk", content = Text("\n\n" + note.Text) });
    }

    [JsonRpcMethod("task/finished", UseSingleObjectParameterDeserialization = true)]
    public void TaskFinished(TextNote note) => HostNotice(note);

    /// <summary>
    /// The approval card, as <c>session/request_permission</c>: the call it belongs to with the files' real changes, and
    /// the three answers of the card. A cancelled request is a refusal: fail closed.
    /// </summary>
    [JsonRpcMethod("approval/request", UseSingleObjectParameterDeserialization = true)]
    public async Task<int> ApprovalRequest(ApprovalNote note)
    {
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
                options = new object[]
                {
                    new { optionId = "allow_once",   name = Strings.ApprovalAllowOnce,   kind = "allow_once" },
                    new { optionId = "allow_always", name = Strings.ApprovalAlwaysAllow, kind = "allow_always" },
                    new { optionId = "reject_once",  name = Strings.ApprovalDeny,        kind = "reject_once" },
                },
            });
            var decision = answer?.Outcome is { Outcome: "selected" } o ? o.OptionId switch
            {
                "allow_once"   => 1,
                "allow_always" => 2,
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

    public sealed record TextNote(string? Text);
    public sealed record PlanNote(string? Goal, List<string>? Steps);
    public sealed record StepUpdateNote(int Index, string? Status);
    public sealed record ToolStartNote(string CallId, string Name, string? Input);
    public sealed record ToolEndNote(string CallId, string? Output);
    public sealed record PathNote(string? Path);
    public sealed record CardNote(string? Title, string? Subject);
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
