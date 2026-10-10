using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Inferpal.Localization;
using Inferpal.Services;
using StreamJsonRpc;

namespace Inferpal.Host.Acp;

/// <summary>
/// Inferpal as an Agent Client Protocol agent (<c>Inferpal.Host --acp</c>): the target of the client's requests —
/// <c>initialize</c>, <c>session/*</c>, <c>authenticate</c> — each session a <see cref="AcpSession"/> of its own.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ One queue for everything sent to the client. Notifications written straight to the connection could overtake
/// one another (its writer is not first-come, first-served), and a <c>tool_call_update</c> that arrives before its
/// <c>tool_call</c> is a card the client cannot find. Requests to the client wait for the queue too: the call a
/// permission is about is on screen before the question.
/// </para>
/// <para>
/// ⚠ Sessions are bounded: each holds a host with its MCP servers and index, and a client keeps every thread it ever
/// opened. Past <see cref="MaxLiveSessions"/>, the idle session used longest ago is closed — saved after every turn, it
/// comes back whole with <c>session/load</c>.
/// </para>
/// </remarks>
internal sealed class AcpAgent : IAsyncDisposable
{
    internal const int MaxLiveSessions = 4;
    private const int ProtocolVersion = 1;

    private readonly Func<HostServer> _hostFactory;
    private readonly Func<CancellationToken, Task<bool>> _ready;
    private readonly ConcurrentDictionary<string, AcpSession> _sessions = new(StringComparer.Ordinal);
    private readonly Channel<Func<Task>> _outbox = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _sender;
    private JsonRpc? _rpc;
    private int _limitsSaid;

    /// <param name="hostFactory">The host each session drives (test seam).</param>
    /// <param name="ready">Whether Inferpal can answer without being set up first (<see cref="AcpReadiness"/>).</param>
    public AcpAgent(Func<HostServer>? hostFactory = null, Func<CancellationToken, Task<bool>>? ready = null)
    {
        _hostFactory = hostFactory ?? (() => new HostServer());
        _ready       = ready ?? AcpReadiness.IsReadyAsync;
        _sender      = Task.Run(SendLoopAsync);
    }

    public void Attach(JsonRpc rpc) => _rpc = rpc;

    internal HostServer CreateHost() => _hostFactory();

    /// <summary>The client reads files for its agents (<c>fs/read_text_file</c>): unsaved buffers can be asked about.</summary>
    internal bool ClientReadsFiles { get; private set; }

    /// <summary>The client can run the setup in a terminal (<c>auth.terminal</c>).</summary>
    internal bool ClientRunsTerminalAuth { get; private set; }

    /// <summary>The client as the model is told it ("Zed").</summary>
    internal string EditorName { get; private set; } = "an editor using the Agent Client Protocol";

    /// <summary>The client for diagnostics ("zed/0.210.4").</summary>
    internal string? ClientName { get; private set; }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
    public object Initialize(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) throw new ArgumentException("initialize takes an object.");
        var caps = Obj(p, "clientCapabilities");
        ClientReadsFiles       = Bool(Obj(caps, "fs"), "readTextFile");
        ClientRunsTerminalAuth = Bool(Obj(caps, "auth"), "terminal") || Bool(Obj(caps, "_meta"), "terminal-auth");
        var info = Obj(p, "clientInfo");
        if ((Str(info, "title") ?? Str(info, "name")) is { Length: > 0 } editor) EditorName = editor;
        ClientName = Str(info, "name") is { } n ? n + (Str(info, "version") is { } v ? "/" + v : string.Empty) : null;

        // Only version 1 exists here: it is the answer whatever was asked, as the protocol says.
        return new
        {
            protocolVersion   = ProtocolVersion,
            agentCapabilities = new
            {
                loadSession         = true,
                promptCapabilities  = new { image = false, audio = false, embeddedContext = true },
                mcpCapabilities     = new { http = true, sse = false },
                sessionCapabilities = new { list = new { }, close = new { }, delete = new { }, resume = new { } },
            },
            agentInfo   = new { name = "inferpal", title = "Inferpal", version = Version },
            // The setup runs in a terminal — offered only to a client that says it can run one.
            authMethods = ClientRunsTerminalAuth
                ? new object[]
                {
                    new
                    {
                        id = AcpReadiness.SetupMethod, name = Strings.AcpSetupMethodName,
                        description = Strings.AcpSetupMethodHint, type = "terminal", args = new[] { "--setup" },
                    },
                }
                : [],
        };
    }

    internal static string Version =>
        typeof(AcpAgent).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// <c>authenticate</c>: the setup is a terminal method, which a client runs on its own and never authenticates —
    /// asked anyway, it is accepted once Inferpal is set up.
    /// </summary>
    [JsonRpcMethod("authenticate", UseSingleObjectParameterDeserialization = true)]
    public async Task<object> AuthenticateAsync(JsonElement p, CancellationToken ct)
    {
        if (Str(p, "methodId") != AcpReadiness.SetupMethod) throw new ArgumentException("Unknown authentication method.");
        if (!await _ready(ct)) throw AcpRpc.Error(AcpRpc.AuthRequired, Strings.AcpNotSetUp(AcpReadiness.DefaultServer));
        return new { };
    }

    // ── Sessions ───────────────────────────────────────────────────────────────

    [JsonRpcMethod("session/new", UseSingleObjectParameterDeserialization = true)]
    public async Task<object> NewSessionAsync(JsonElement p, CancellationToken ct)
    {
        var cwd = Cwd(p);
        var servers = McpServers(p);
        // ⚠ Gated only for a client that can run the setup: without it, refusing would leave no way forward — the turn
        // then says what is missing, as it does in the editors.
        if (ClientRunsTerminalAuth && !await _ready(ct))
            throw AcpRpc.Error(AcpRpc.AuthRequired, Strings.AcpNotSetUp(AcpReadiness.DefaultServer));

        var session = await OpenAsync(NewId(), cwd, servers, ct);
        AnnounceCommandsSoon(session);
        return new { sessionId = session.Id, modes = session.ModeState(), configOptions = session.ConfigOptions() };
    }

    [JsonRpcMethod("session/load", UseSingleObjectParameterDeserialization = true)]
    public Task<object> LoadSessionAsync(JsonElement p, CancellationToken ct) => ResumeOrLoadAsync(p, replay: true, ct);

    [JsonRpcMethod("session/resume", UseSingleObjectParameterDeserialization = true)]
    public Task<object> ResumeSessionAsync(JsonElement p, CancellationToken ct) => ResumeOrLoadAsync(p, replay: false, ct);

    private async Task<object> ResumeOrLoadAsync(JsonElement p, bool replay, CancellationToken ct)
    {
        var id  = Str(p, "sessionId") ?? throw new ArgumentException("'sessionId' is required.");
        var cwd = Cwd(p);
        if (_sessions.TryGetValue(id, out var live))
        {
            if (replay) { live.ReplayLive(); await FlushAsync(); }
        }
        else
        {
            // Not gated: reading a conversation back needs no model server — the next turn says what is missing.
            live = await OpenAsync(id, cwd, McpServers(p), ct);
            if (!await live.LoadAsync(replay, ct))
            {
                await CloseAsync(id);
                throw AcpRpc.Error(AcpRpc.NotFound, Strings.AcpSessionNotFound(id));
            }
        }
        // Before the response: the client named the session, and nothing may follow a load's answer.
        await live.AnnounceCommandsOnceAsync(ct);
        await FlushAsync();
        return new { modes = live.ModeState(), configOptions = live.ConfigOptions() };
    }

    /// <summary>
    /// <c>session/list</c>: the saved sessions that belong to a folder (the client's <c>cwd</c>, or any), and the ones
    /// this process holds that were not saved yet. A session saved without its folder is listed under none.
    /// </summary>
    [JsonRpcMethod("session/list", UseSingleObjectParameterDeserialization = true)]
    public async Task<object> ListSessionsAsync(JsonElement p, CancellationToken ct)
    {
        var filter = Str(p, "cwd");
        var listed = HostServer.ToSessionList(await new Services.Persistence.ConversationStore().ListWithPreviewAsync(ct));
        var items = new List<object>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in listed.Sessions)
        {
            if (string.IsNullOrWhiteSpace(s.WorkspaceRoot) || !SameFolder(filter, s.WorkspaceRoot)) continue;
            seen.Add(s.Name);
            items.Add(new
            {
                sessionId = s.Name, cwd = s.WorkspaceRoot,
                title     = string.IsNullOrWhiteSpace(s.Preview) ? null : s.Preview,
                updatedAt = s.SavedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            });
        }
        foreach (var live in _sessions.Values)
            if (!seen.Contains(live.Id) && SameFolder(filter, live.Cwd))
                items.Add(new { sessionId = live.Id, cwd = live.Cwd });
        return new { sessions = items };
    }

    private static bool SameFolder(string? filter, string folder) =>
        filter is null || PathComparer.SameDirectory(filter, folder);

    [JsonRpcMethod("session/close", UseSingleObjectParameterDeserialization = true)]
    public async Task<object> CloseSessionAsync(JsonElement p)
    {
        await CloseAsync(Str(p, "sessionId") ?? throw new ArgumentException("'sessionId' is required."));
        return new { };
    }

    /// <summary><c>session/delete</c>: the session closed and its file removed; an unknown id is no error.</summary>
    [JsonRpcMethod("session/delete", UseSingleObjectParameterDeserialization = true)]
    public async Task<object> DeleteSessionAsync(JsonElement p)
    {
        var id = Str(p, "sessionId") ?? throw new ArgumentException("'sessionId' is required.");
        await CloseAsync(id);
        try { new Services.Persistence.ConversationStore().Delete(id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Diagnostics.Swallow("AcpAgent.Delete", ex); }
        return new { };
    }

    [JsonRpcMethod("session/prompt", UseSingleObjectParameterDeserialization = true)]
    public async Task<object> PromptAsync(JsonElement p)
    {
        var session = Live(p);
        if (!p.TryGetProperty("prompt", out var prompt)) throw new ArgumentException("'prompt' is required.");
        return new { stopReason = await session.PromptAsync(prompt) };
    }

    [JsonRpcMethod("session/cancel", UseSingleObjectParameterDeserialization = true)]
    public async Task CancelAsync(JsonElement p)
    {
        if (Str(p, "sessionId") is { } id && _sessions.TryGetValue(id, out var session)) await session.CancelAsync();
    }

    [JsonRpcMethod("session/set_mode", UseSingleObjectParameterDeserialization = true)]
    public async Task<object> SetModeAsync(JsonElement p, CancellationToken ct)
    {
        await Live(p).SetModeAsync(Str(p, "modeId") ?? throw new ArgumentException("'modeId' is required."), ct);
        return new { };
    }

    [JsonRpcMethod("session/set_config_option", UseSingleObjectParameterDeserialization = true)]
    public async Task<object> SetConfigOptionAsync(JsonElement p, CancellationToken ct)
    {
        var configId = Str(p, "configId") ?? throw new ArgumentException("'configId' is required.");
        if (!p.TryGetProperty("value", out var value)) throw new ArgumentException("'value' is required.");
        return new { configOptions = await Live(p).SetConfigOptionAsync(configId, value, ct) };
    }

    /// <summary>
    /// <c>session/set_model</c>: withdrawn from the stable protocol in favour of the <c>model</c> option, and still what
    /// some clients send (CodeCompanion) — the same choice as <see cref="SetConfigOptionAsync"/>.
    /// </summary>
    [JsonRpcMethod("session/set_model", UseSingleObjectParameterDeserialization = true)]
    public async Task<object> SetModelAsync(JsonElement p, CancellationToken ct)
    {
        var model = Str(p, "modelId") ?? throw new ArgumentException("'modelId' is required.");
        await Live(p).SetConfigOptionAsync(AcpModes.ModelOption, JsonSerializer.SerializeToElement(model), ct);
        return new { };
    }

    private AcpSession Live(JsonElement p)
    {
        var id = Str(p, "sessionId") ?? throw new ArgumentException("'sessionId' is required.");
        return _sessions.TryGetValue(id, out var s) ? s : throw new ArgumentException($"Unknown session '{id}'.");
    }

    private async Task<AcpSession> OpenAsync(string id, string cwd, IReadOnlyList<McpSessionServerDto> servers, CancellationToken ct)
    {
        await EvictIdleAsync();
        var session = await AcpSession.StartAsync(this, id, cwd, servers, ct);
        if (!_sessions.TryAdd(id, session))
        {
            await session.DisposeAsync();
            return _sessions[id];
        }
        return session;
    }

    /// <summary>Room for one more session: the idle one used longest ago is closed (it is saved; load brings it back).</summary>
    private async Task EvictIdleAsync()
    {
        while (_sessions.Count >= MaxLiveSessions)
        {
            var idle = _sessions.Values.Where(s => !s.Busy).OrderBy(s => s.LastUsed).FirstOrDefault();
            if (idle is null) return;   // all busy: one more, rather than stopping a turn
            await CloseAsync(idle.Id);
        }
    }

    /// <summary>
    /// A session closed: its running turn is cancelled and allowed to answer <c>cancelled</c> first — torn down under
    /// it, the turn would answer with a lost connection instead, an error the protocol does not allow.
    /// </summary>
    private async Task CloseAsync(string id)
    {
        if (!_sessions.TryRemove(id, out var session)) return;
        await session.CancelAsync();
        await session.WhenIdleAsync(CloseGrace);
        await session.DisposeAsync();
    }

    /// <summary>How long a closed session's turn is given to end on its own.</summary>
    internal static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The commands go out once the client has the session's id: sent from the request that creates it, they would
    /// reach the client before the response that names the session.
    /// </summary>
    private void AnnounceCommandsSoon(AcpSession session) => _ = Task.Run(async () =>
    {
        await Task.Delay(100);
        await session.AnnounceCommandsOnceAsync(CancellationToken.None);
    });

    private static string NewId() =>
        $"acp-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{Guid.NewGuid().ToString("N")[..6]}";

    // ── To the client ──────────────────────────────────────────────────────────

    /// <summary>A notification, in order behind everything already queued.</summary>
    internal void Notify(string method, object payload)
    {
        if (_rpc is not { } rpc) return;
        _outbox.Writer.TryWrite(() => rpc.NotifyWithParameterObjectAsync(method, payload));
    }

    /// <summary>Everything queued so far is on the wire.</summary>
    internal Task FlushAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_outbox.Writer.TryWrite(() => { done.TrySetResult(); return Task.CompletedTask; })) done.TrySetResult();
        return done.Task;
    }

    /// <summary>A request to the client, after everything already queued.</summary>
    internal async Task<T?> RequestAsync<T>(string method, object payload, CancellationToken ct = default)
    {
        var rpc = _rpc ?? throw new InvalidOperationException("Not attached.");
        await FlushAsync();
        return await rpc.InvokeWithParameterObjectAsync<T?>(method, payload, ct);
    }

    private async Task SendLoopAsync()
    {
        await foreach (var send in _outbox.Reader.ReadAllAsync())
        {
            try { await send(); }
            catch (Exception ex) { Diagnostics.Swallow("AcpAgent.Send", ex); }
        }
    }

    /// <summary>What Inferpal does not do in an ACP client, said once per process, after the first answer.</summary>
    internal string? TakeLimitsNotice() =>
        Interlocked.Exchange(ref _limitsSaid, 1) == 0 ? Strings.AcpLimitsNotice(AcpReadiness.DocsUrl) : null;

    // ── Reading params ──────────────────────────────────────────────────────────

    private static JsonElement Obj(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    private static bool Bool(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The session's folder: required, absolute (the protocol says so), and a folder that exists.</summary>
    private static string Cwd(JsonElement p)
    {
        var cwd = Str(p, "cwd");
        if (string.IsNullOrWhiteSpace(cwd) || !Path.IsPathRooted(cwd)) throw new ArgumentException("'cwd' must be an absolute path.");
        return Path.GetFullPath(cwd);
    }

    /// <summary>The editor's MCP servers (<c>mcpServers</c>): stdio and HTTP; an SSE one is not supported, and skipped.</summary>
    internal static IReadOnlyList<McpSessionServerDto> McpServers(JsonElement p)
    {
        var list = new List<McpSessionServerDto>();
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("mcpServers", out var servers) || servers.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var s in servers.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object || Str(s, "name") is not { Length: > 0 } name) continue;
            var type = Str(s, "type");
            if (type == "http" && Str(s, "url") is { } url)
                list.Add(new McpSessionServerDto(name, Url: url, Headers: Pairs(s, "headers")));
            else if (type is null && Str(s, "command") is { } command)
                list.Add(new McpSessionServerDto(name, command, StringList(s, "args"), Pairs(s, "env")));
            else
                Diagnostics.Record("Acp", $"MCP server '{name}' from the editor was not started: transport '{type ?? "?"}' is not supported here.");
        }
        return list;
    }

    private static List<string> StringList(JsonElement o, string name) =>
        o.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? [.. a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
            : [];

    /// <summary><c>[{name, value}]</c> as a dictionary (env, headers).</summary>
    private static Dictionary<string, string> Pairs(JsonElement o, string name)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (o.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array)
            foreach (var x in a.EnumerateArray())
                if (Str(x, "name") is { Length: > 0 } k && Str(x, "value") is { } v) d[k] = v;
        return d;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToList()) await CloseAsync(id);
        _outbox.Writer.TryComplete();
        try { await _sender.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
    }
}
