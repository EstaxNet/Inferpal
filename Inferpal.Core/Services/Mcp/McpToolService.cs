using Inferpal.Config;
using Inferpal.Services.Mcp.OAuth;
using Inferpal.Services.Tools;

using Inferpal.Localization;

namespace Inferpal.Services.Mcp;

/// <summary>Connection status of one configured MCP server, for the settings UI.</summary>
internal sealed record McpServerStatus(string Name, bool Connected, int ToolCount, string? Error, bool AuthRequired = false);

/// <summary>
/// Singleton that owns the lifecycle of all configured MCP servers: spawns them, discovers
/// their tools, and exposes those tools as <see cref="ITool"/> instances for <see cref="ToolRegistry"/>.
/// </summary>
/// <remarks>
/// Initialization runs in the background (constructor fire-and-forget) so opening the tool window
/// never blocks on a slow server. <see cref="Tools"/> is empty until discovery completes, and the
/// agent simply sees the MCP tools appear once they are ready. Servers that advertise
/// <c>tools.listChanged</c> trigger a live re-discovery (no settings save needed) — see
/// <see cref="OnServerToolsChanged"/> — and a server whose process dies mid-session is
/// auto-reconnected with backoff — see <see cref="OnServerClosed"/>.
/// </remarks>
internal sealed class McpToolService : IAsyncDisposable
{
    /// <summary>Default backoff schedule between reconnect attempts after a server dies. One attempt
    /// per slot; when the schedule is exhausted the server is left disconnected until the next save.</summary>
    private static readonly TimeSpan[] DefaultReconnectBackoff =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30),
    ];

    private readonly InferpalConfig _config;
    private readonly IApprovalService  _approval;
    private readonly Func<McpServerConfig, IMcpClient> _clientFactory;
    private readonly IReadOnlyList<TimeSpan> _reconnectBackoff;
    private readonly McpTokenStore _tokenStore;
    /// <summary>The server list as the configuration FILE holds it now — the other editor writes it too.</summary>
    private readonly Func<string?> _sharedServersJson;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// The workspace whose repository's servers (<see cref="RepoMcpServers"/>) start beside the configured ones — after
    /// the user agrees. Set by the front-end once it knows it (Visual Studio pins it after the service exists).
    /// </summary>
    public Func<string?>? WorkspaceRoot { get; set; }

    /// <summary>Asks the user for a value a repository's server declares (<c>${input:id}</c>); <c>null</c> = not
    /// given. Set by the front-end; without it, such a server does not start, and says why.</summary>
    public Func<RepoMcpServer, RepoMcpInput, CancellationToken, Task<string?>>? AskInput { get; set; }

    /// <summary>The agreements given to start a repository's servers. Test seam.</summary>
    internal RepoMcpConsents Consents { get; set; } = new();

    /// <summary>The <c>${input:…}</c> values given in this process, per repository, server and input: asked once per
    /// session, never written anywhere.</summary>
    private readonly Dictionary<string, string> _inputValues = new(StringComparer.Ordinal);

    /// <summary>The authorization metadata address each server announced when it refused us (see
    /// <see cref="IMcpClient.ResourceMetadataUrl"/>), for the sign-in that follows: by then its client is gone.</summary>
    /// ⚠ Keyed by name AND address: a name pointed at another server kept the old one's announcement, and the sign-in
    /// that followed discovered the wrong authorization server.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Name, string? Url), string> _announcedMetadata = new();

    /// <summary>Per-server state. Mutated only under <see cref="_gate"/> (except the reconnect guard,
    /// which is lock-free so the read-loop thread can claim it without blocking).</summary>
    private sealed class ServerEntry(McpServerConfig config, IMcpClient client)
    {
        public McpServerConfig Config { get; } = config;
        public IMcpClient Client { get; set; } = client;
        public IReadOnlyList<ITool> Tools { get; set; } = [];
        public bool Connected { get; set; } = true;
        public string? Error { get; set; }
        /// <summary>The server refused its sign-in during the session: the card offers "Sign in" again.</summary>
        public bool AuthRequired { get; set; }

        private int _reconnecting;
        private IMcpClient? _closedDuringReconnect;

        /// <summary>Claims the single in-flight reconnect slot; returns false if one is already running.</summary>
        public bool TryBeginReconnect() => Interlocked.CompareExchange(ref _reconnecting, 1, 0) == 0;
        public void EndReconnect() => Interlocked.Exchange(ref _reconnecting, 0);

        /// <summary>
        /// A client closed while a reconnect held the slot. ⚠ Dropping that signal published the server
        /// "connected" with no tools when the client being reconnected died during its own listing —
        /// and nothing ever reconnected it. The reconnect re-checks it when it releases the slot.
        /// </summary>
        public void NoteClosedDuringReconnect(IMcpClient client) => Volatile.Write(ref _closedDuringReconnect, client);
        public IMcpClient? TakeClosedDuringReconnect() => Interlocked.Exchange(ref _closedDuringReconnect, null);
    }

    private List<ServerEntry> _servers = [];
    private IReadOnlyList<McpServerStatus> _failed = [];   // servers that could not be started at all
    // Entries REJECTED while reading the configuration: they never were servers, so _failed could
    // not carry them - and they appeared nowhere. They join the same snapshot, so both front-ends
    // render them without a line of extra code.
    private IReadOnlyList<McpServerStatus> _rejected = [];
    // Tools and status are published as ONE snapshot. Two separate writes let a reader see the tools of a
    // reconnected server with the status from before the reconnect ("disconnected", zero tools).
    private sealed record Snapshot(IReadOnlyList<ITool> Tools, IReadOnlyList<McpServerStatus> Status);
    private volatile Snapshot _snapshot = new([], []);
    // volatile: re-checked after awaits on threads other than the disposing one.
    private volatile bool _disposed;

    public McpToolService(InferpalConfig config, IApprovalService approval)
        : this(config, approval, clientFactory: null) { }

    /// <summary>Picks the transport from the server entry: a <c>url</c> ⇒ Streamable HTTP (with an OAuth
    /// token provider bound to the encrypted store), otherwise stdio.</summary>
    private IMcpClient DefaultClientFactory(McpServerConfig cfg) =>
        cfg.IsHttp ? new McpHttpClient(cfg, tokenProvider: TokenProviderFor(cfg)) : new McpStdioClient(cfg);

    private McpStoredTokenProvider TokenProviderFor(McpServerConfig cfg) =>
        new(cfg.Name, McpOAuthMetadata.CanonicalResource(new Uri(cfg.Url!)), _tokenStore, new McpOAuthFlow(new RefreshOnlyReceiver()));

    /// <summary>Test seam: injects the client transport (production uses the transport factory above)
    /// and, optionally, a faster reconnect-backoff schedule and token store.</summary>
    internal McpToolService(InferpalConfig config, IApprovalService approval,
                            Func<McpServerConfig, IMcpClient>? clientFactory,
                            IReadOnlyList<TimeSpan>? reconnectBackoff = null,
                            McpTokenStore? tokenStore = null,
                            Func<string?>? sharedServersJson = null,
                            Func<string?>? workspaceRoot = null,
                            Func<RepoMcpServer, RepoMcpInput, CancellationToken, Task<string?>>? askInput = null)
    {
        // Before the first refresh below, which reads both.
        WorkspaceRoot      = workspaceRoot;
        AskInput           = askInput;
        _config            = config;
        _approval          = approval;
        _clientFactory     = clientFactory ?? DefaultClientFactory;
        _reconnectBackoff  = reconnectBackoff ?? DefaultReconnectBackoff;
        _tokenStore        = tokenStore ?? new McpTokenStore(McpTokenStore.DefaultPath);
        _sharedServersJson = sharedServersJson ?? (() => InferpalConfig.Load().McpServersJson);
        if (_config.McpEnabled)
            _ = RefreshAsync();
    }

    /// <summary>A receiver used only on the refresh path (provider), where no browser is launched.</summary>
    private sealed class RefreshOnlyReceiver : IAuthCodeReceiver
    {
        public string RedirectUri => "http://127.0.0.1/callback";
        public Task<(string Code, string State)> GetAuthorizationCodeAsync(string authorizationUrl, CancellationToken ct)
            => throw new InvalidOperationException("Interactive authorization is not available on the refresh path.");
    }

    /// <summary>Live snapshot of all MCP tools currently available (empty until discovery completes).</summary>
    public IReadOnlyList<ITool> Tools => _snapshot.Tools;

    /// <summary>Per-server connection status, for display in the settings window.</summary>
    public IReadOnlyList<McpServerStatus> Status => _snapshot.Status;

    /// <summary>
    /// One line per configured server for the <c>/diagnostics export</c> bundle. English by
    /// doctrine — its audience is a public issue tracker, not the chat locale.
    /// </summary>
    /// <remarks>
    /// ⚠ The bundle only carried "MCP: on": a declared server that had
    /// not started produced a report where everything looks normal and the expected tools are
    /// missing, without a word about the cause. The rendering lives here rather than in the
    /// handler because <c>DiagnosticsCommandHandler</c> is pure by doctrine: state is passed to it.
    /// </remarks>
    public IReadOnlyList<string> DescribeForBundle() =>
        [.. _snapshot.Status.Select(s => s.Connected
            ? s.Error is { Length: > 0 } problem
                ? $"{s.Name} — connected, {s.ToolCount} tool(s): {problem}"
                : $"{s.Name} — connected, {s.ToolCount} tool(s)"
            : $"{s.Name} — NOT connected: {NotConnectedReason(s)}")];

    /// <summary>Why <paramref name="s"/> cannot serve anything, or <c>null</c> when it can.</summary>
    /// <remarks>
    /// One wording for every reader of that question: the support bundle, and the model when it calls
    /// a tool of a server that went away.
    /// </remarks>
    internal static string? NotConnectedReason(McpServerStatus s) =>
        s.Connected             ? null
      : s.AuthRequired          ? "authorization required"
      : s.Error is { Length: > 0 } e ? e
      : "no reason reported";

    /// <summary>
    /// What to tell the <b>model</b> when it calls a tool name no registry holds — <c>null</c> when no
    /// configured server claims that name, which is the only case that really is an unknown tool.
    /// </summary>
    /// <remarks>
    /// ⚠ "Unknown tool" means "you invented this name". A tool served by an MCP server that went away
    /// mid-run is not invented: the model READ it in its own tool list, and the reason it is gone sits
    /// one field away in this very object. Told otherwise, the model looks for another name instead of
    /// saying the server is down — the same silence <c>/diagnostics</c> and the support bundle were
    /// taught to break, held by two of its three readers.
    /// </remarks>
    public string? DescribeMissingTool(string toolName)
    {
        foreach (var s in Status)
        {
            if (!McpTool.BelongsTo(toolName, s.Name)) continue;

            return NotConnectedReason(s) is { } reason
                ? $"Tool '{toolName}' is unavailable: its MCP server '{s.Name}' is not connected — "
                  + $"{reason}. Do not retry it; say so and continue without it."
                : $"Tool '{toolName}' is not offered by MCP server '{s.Name}', which is connected "
                  + $"with {s.ToolCount} tool(s). Use one of the names in your tool list.";
        }
        return null;
    }

    /// <summary>
    /// Tears down any running servers and re-connects from the current config. Safe to call
    /// repeatedly (e.g. after the user edits MCP settings); calls are serialized.
    /// </summary>
    public async Task RefreshAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await TeardownAsync().ConfigureAwait(false);

            if (_disposed || !_config.McpEnabled)
            {
                _snapshot = new Snapshot([], []);
                return;
            }

            var servers = McpServerConfig.Parse(_config.McpServersJson, out var rejected);
            _rejected = [.. rejected.Select(r => new McpServerStatus(r.Name, false, 0, r.Reason))];

            // ⚠ A declared server that does not start must leave a readable trace. Filed in
            // McpServerStatus.Error alone, it is read by the Visual Studio settings window and by
            // nothing else: in VS Code there is no panel, so the user simply sees their tools
            // missing. The /diagnostics channel exists on BOTH sides — it is the floor, and where
            // someone wondering why their tools are missing eventually looks.
            foreach (var r in _rejected)
                Diagnostics.Record("Mcp", $"Server '{r.Name}' rejected by the configuration: {r.Error}");
            // The repository's servers: after the user's own (a name the user configured is theirs), each one asked
            // about before it runs anything.
            var repository = await RepositoryServersAsync(
                [.. servers.Select(s => s.Name), .. rejected.Select(r => r.Name)]).ConfigureAwait(false);
            ForgetSignInsOfRemovedServers(servers, rejected, repository.Names);
            // ⚠ In PARALLEL, keeping the configured order. Each start has its own handshake budget:
            // serially, an unreachable server made every later one pay it, lock held — and so did
            // the Save button, which waits for this refresh.
            var started = await Task.WhenAll(servers.Where(s => s.Enabled).Concat(repository.Start).Select(StartServerAsync))
                                    .ConfigureAwait(false);

            _servers = [.. started.Where(r => r.Entry is not null).Select(r => r.Entry!)];
            _failed  = [.. started.Where(r => r.Failure is not null).Select(r => r.Failure!), .. repository.NotStarted];
            RebuildSnapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A server taken out of the configuration takes its sign-in with it.
    /// </summary>
    /// <remarks>
    /// ⚠ Kept, its access and refresh tokens stay on disk for a service the user no longer uses — nothing else ever
    /// removes them. Only when the list is KNOWN: a list that does not parse says nothing about what was removed, and an
    /// entry the parser rejected is still being written (its name keeps its sign-in). MCP turned off never reaches here.
    /// ⚠ The token file is SHARED by the two editors, the configuration in memory is THIS process's: a server the other
    /// editor has just added — and signed in to — is absent from it, and judged by it alone its sign-in would be erased
    /// at the next settings save here. "Gone" is gone from BOTH: the list in memory and the list the file holds now.
    /// </remarks>
    private void ForgetSignInsOfRemovedServers(IReadOnlyList<McpServerConfig> servers, IReadOnlyList<McpRejectedServer> rejected,
                                               IReadOnlyCollection<string> repositoryNames)
    {
        if (rejected.Any(r => r.Name == McpServerConfig.WholeList)) return;   // the whole list is unreadable
        try
        {
            var shared = McpServerConfig.Parse(_sharedServersJson(), out var sharedRejected);
            if (sharedRejected.Any(r => r.Name == McpServerConfig.WholeList)) return;   // the file's list is unreadable
            // A repository's server signed in to keeps its sign-in while the repository declares it.
            var known = servers.Select(s => s.Name).Concat(rejected.Select(r => r.Name)).Concat(repositoryNames)
                               .Concat(shared.Select(s => s.Name)).Concat(sharedRejected.Select(r => r.Name))
                               .ToHashSet(StringComparer.Ordinal);
            var gone  = _tokenStore.RemoveAllExcept(known);
            if (gone.Count > 0)
                Diagnostics.Record("Mcp",
                    $"Sign-in removed for {gone.Count} MCP server(s) no longer configured: {string.Join(", ", gone)}.");
        }
        catch (Exception ex) { Diagnostics.Swallow("McpToolService.ForgetSignIns", ex); }
    }

    /// <summary>
    /// Starts one server and discovers its tools. Never throws: a refused start, a missing
    /// authorization or an exception during the handshake or the listing becomes that server's
    /// status, and its client is disposed.
    /// </summary>
    private async Task<(ServerEntry? Entry, McpServerStatus? Failure)> StartServerAsync(McpServerConfig server)
    {
        IMcpClient? client = null;
        try
        {
            client = _clientFactory(server);
            if (!await client.StartAsync(CancellationToken.None).ConfigureAwait(false))
            {
                // Two distinct outcomes: "you need to authorize" is an action for the user,
                // "it did not start" is a fault. Conflating them sends people looking in the
                // wrong place.
                Diagnostics.Record("Mcp", client.NeedsAuthorization
                    ? $"Server '{server.Name}' needs authorization: its tools are not available."
                    : $"Server '{server.Name}' did not start: {client.LastError}");
                if (client.ResourceMetadataUrl is { Length: > 0 } announced) _announcedMetadata[(server.Name, server.Url)] = announced;
                var failure = new McpServerStatus(server.Name, false, 0, client.LastError, client.NeedsAuthorization);
                await client.DisposeAsync().ConfigureAwait(false);
                return (null, failure);
            }

            // Wire lifecycle events before discovery so a death mid-listing still triggers reconnect.
            var entry  = new ServerEntry(server, client);
            var events = client;
            client.ToolsChanged          += () => OnServerToolsChanged(entry);
            client.Closed                += () => OnServerClosed(entry, events);
            client.AuthorizationRequired += () => OnServerSignInRefused(entry, events);

            var listed = await client.ListToolsAsync(CancellationToken.None).ConfigureAwait(false);
            if (listed is null)
            {
                // Started but not listed: "connected, 0 tools" alone reads as a server with nothing to offer.
                entry.Error = $"its tool list could not be read: {client.LastError}";
                Diagnostics.Record("Mcp", $"Server '{server.Name}' started, but {entry.Error}");
            }
            entry.Tools = BuildTools(client, listed ?? []);
            return (entry, null);
        }
        catch (Exception ex)
        {
            // A server that throws fails ON ITS OWN: without this catch, Task.WhenAll rethrows, no
            // server is published, and the clients already started are attached to nothing.
            Diagnostics.Record("Mcp", $"Server '{server.Name}' did not start: {ex.Message}");
            if (client is not null)
            {
                try { await client.DisposeAsync().ConfigureAwait(false); }
                catch (Exception disposeEx) { Diagnostics.Swallow($"McpToolService.StartServer({server.Name})", disposeEx); }
            }
            return (null, new McpServerStatus(server.Name, false, 0, ex.Message));
        }
    }

    /// <summary>
    /// The repository's servers to start, the ones that will not — each with why — and every name it declares.
    /// </summary>
    /// <remarks>
    /// ⚠ The agreement is FORCED (<c>forcePrompt</c>): no <c>allow</c> rule, no "don't ask", no "Always" given to
    /// another tool starts a program the repository chose. Given once per <see cref="RepoMcpServer.Fingerprint"/> and
    /// kept (<see cref="RepoMcpConsents"/>); a declined server is asked about again at the next start.
    /// </remarks>
    private async Task<(List<McpServerConfig> Start, List<McpServerStatus> NotStarted, List<string> Names)> RepositoryServersAsync(
        IReadOnlyCollection<string> configured)
    {
        var start = new List<McpServerConfig>();
        var notStarted = new List<McpServerStatus>();
        var names = new List<string>();
        RepoMcpScan scan;
        try { scan = RepoMcpServers.Read(WorkspaceRoot?.Invoke()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("McpToolService.RepositoryServers", ex);
            return (start, notStarted, names);
        }
        if (scan.Root is not { } root) return (start, notStarted, names);

        foreach (var p in scan.Problems)
        {
            notStarted.Add(new McpServerStatus(p.Name ?? p.Source, false, 0, $"{p.Source}: {p.Reason}"));
            Diagnostics.Record("Mcp", $"Repository MCP file {p.Source}{(p.Name is null ? "" : $", server '{p.Name}'")} ignored: {p.Reason}.");
        }

        var taken = new HashSet<string>(configured, StringComparer.Ordinal);
        foreach (var server in scan.Servers)
        {
            names.Add(server.Name);
            if (!taken.Add(server.Name))
            {
                notStarted.Add(new McpServerStatus(server.Name, false, 0,
                    $"{server.Source}: not started — a server named '{server.Name}' is declared earlier, and that one runs"));
                continue;
            }
            if (!Consents.IsAgreed(root, server))
            {
                bool agreed;
                try
                {
                    agreed = await _approval.RequestApprovalAsync("mcp_server",
                        Strings.McpRepoServerQuestion(server.Name, server.Source, server.Runs), CancellationToken.None,
                        subject: server.Runs, forcePrompt: true).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Diagnostics.Swallow("McpToolService.RepositoryServerAgreement", ex);
                    agreed = false;
                }
                if (!agreed)
                {
                    notStarted.Add(new McpServerStatus(server.Name, false, 0,
                        $"{server.Source}: not started — it was not agreed to; asked again at the next start"));
                    Diagnostics.Record("Mcp", $"Repository MCP server '{server.Name}' ({server.Source}) not started: not agreed to.");
                    continue;
                }
                Consents.Agree(root, server);
            }

            var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
            string? unanswered = null;
            foreach (var input in server.Inputs)
            {
                var key = $"{root}|{server.Name}|{input.Id}";
                if (!_inputValues.TryGetValue(key, out var value))
                {
                    value = AskInput is { } ask ? await AskSafelyAsync(ask, server, input).ConfigureAwait(false) : null;
                    if (value is null) { unanswered = input.Id; break; }
                    _inputValues[key] = value;
                }
                inputs[input.Id] = value;
            }
            if (unanswered is not null)
            {
                notStarted.Add(new McpServerStatus(server.Name, false, 0,
                    $"{server.Source}: not started — the value it asks for (input '{unanswered}') was not given"));
                continue;
            }

            var (config, missing, rejected) = RepoMcpServers.Prepare(server, root, inputs);
            if (config is null)
            {
                var why = missing.Count > 0 ? $"nothing fills {string.Join(", ", missing)}" : rejected;
                notStarted.Add(new McpServerStatus(server.Name, false, 0, $"{server.Source}: not started — {why}"));
                Diagnostics.Record("Mcp", $"Repository MCP server '{server.Name}' ({server.Source}) not started: {why}.");
                continue;
            }
            start.Add(config);
        }
        return (start, notStarted, names);
    }

    private static async Task<string?> AskSafelyAsync(
        Func<RepoMcpServer, RepoMcpInput, CancellationToken, Task<string?>> ask, RepoMcpServer server, RepoMcpInput input)
    {
        try { return await ask(server, input, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Swallow("McpToolService.AskInput", ex);
            return null;
        }
    }

    /// <summary>
    /// Runs the interactive OAuth authorization (browser) for one HTTP server, persists the resulting
    /// tokens to the encrypted store, then reconnects so its tools load. Throws if the server isn't a
    /// configured HTTP server or the user cancels/denies. Called from the settings "Authorize" action.
    /// </summary>
    public async Task AuthorizeAsync(string serverName, CancellationToken ct = default)
    {
        // Refuse before opening the browser: without a platform secret store the refresh token
        // cannot be persisted, and failing at the end would waste the whole interactive flow.
        if (!_tokenStore.CanProtect)
            throw new InvalidOperationException(Strings.McpOAuthUnsupportedPlatform);

        // A repository's server the user agreed to is signed in to like a configured one.
        var cfg = McpServerConfig.Parse(_config.McpServersJson)
            .Concat(_servers.Select(e => e.Config))
            .FirstOrDefault(s => s.IsHttp && string.Equals(s.Name, serverName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"'{serverName}' is not a configured HTTP MCP server.");

        var server = OAuthServerFor(cfg);

        var flow  = new McpOAuthFlow(new LoopbackAuthCodeReceiver());
        var state = await flow.AuthorizeAsync(server, _tokenStore.Get(serverName), ct).ConfigureAwait(false);
        _tokenStore.Save(serverName, state);

        await RefreshAsync().ConfigureAwait(false);
    }

    /// <summary>What the sign-in to <paramref name="cfg"/> starts from — with the metadata address the server announced
    /// when it refused us, if it did (otherwise the default well-known address is tried).</summary>
    internal McpOAuthServer OAuthServerFor(McpServerConfig cfg) =>
        new(new Uri(cfg.Url!), cfg.OAuth?.ClientId, cfg.OAuth?.ClientSecret, cfg.OAuth?.Scopes,
            _announcedMetadata.TryGetValue((cfg.Name, cfg.Url), out var announced) ? announced : null);

    /// <summary>
    /// Handles a server's <c>tools/list_changed</c> notification: re-runs discovery for that one
    /// server and republishes the aggregate tool list. Fired on the client read-loop thread, so it
    /// hops onto a background task and serializes through <see cref="_gate"/> like every other mutation.
    /// </summary>
    private void OnServerToolsChanged(ServerEntry entry) => _ = RediscoverAsync(entry);

    private async Task RediscoverAsync(ServerEntry entry)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || !_servers.Contains(entry)) return;
            var client = entry.Client;
            var listed = await client.ListToolsAsync(CancellationToken.None).ConfigureAwait(false);
            if (listed is null)
            {
                // A failed or slow re-listing is not "the server has no tools any more": the tools it had stay.
                Diagnostics.Record("Mcp",
                    $"Server '{entry.Config.Name}': its tool list could not be refreshed ({client.LastError}); the previous tools are kept.");
                return;
            }
            entry.Tools = BuildTools(client, listed);
            if (entry.Connected) entry.Error = null;
            RebuildSnapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Handles a server process dying mid-session: drops its (now stale) tools immediately and
    /// attempts to respawn it with backoff. Fired on the read-loop thread; the lock-free guard
    /// ensures only one reconnect runs per server even if <c>Closed</c> races.
    /// </summary>
    /// <param name="client">The client that closed — a close during a reconnect is kept, and acted on
    /// when that reconnect ends if it was the client the reconnect published.</param>
    private void OnServerClosed(ServerEntry entry, IMcpClient client)
    {
        if (_disposed) return;
        if (entry.TryBeginReconnect())
            _ = ReconnectAsync(entry);
        else
            entry.NoteClosedDuringReconnect(client);
    }

    private async Task ReconnectAsync(ServerEntry entry)
    {
        try
        {
            // 1. Drop the dead server's tools right away so the agent stops seeing them.
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed || !_servers.Contains(entry)) return;
                // Why it died, read before the client is gone: said once per server and cause in /diagnostics (a server
                // that crashes on every call is restarted every time, and left no trace), and carried by the status the
                // model reads if it calls a tool of this server before the restart.
                var why = entry.Client.LastError;
                await entry.Client.DisposeAsync().ConfigureAwait(false);
                entry.Tools     = [];
                entry.Connected = false;
                entry.Error     = string.IsNullOrEmpty(why) ? "server exited — reconnecting…" : $"server exited ({why}) — reconnecting…";
                if (!string.IsNullOrEmpty(why))
                    Diagnostics.RecordOnce("Mcp", $"Server '{entry.Config.Name}' stopped: {why}. Restarting it.",
                                           entry.Config.Name + "\u0001" + why);
                RebuildSnapshot();
            }
            finally { _gate.Release(); }

            // 2. Retry with backoff, outside the gate so saves and other servers aren't blocked.
            // ⚠ The last attempt knows why the server will not come back — its exit code, what it wrote on stderr — and
            // that is what the final state says: "reconnect failed" alone sends the user looking for the wrong cause.
            string? lastFailure = null;
            foreach (var delay in _reconnectBackoff)
            {
                await Task.Delay(delay).ConfigureAwait(false);
                if (_disposed || !_servers.Contains(entry)) return;

                var client = _clientFactory(entry.Config);
                if (!await client.StartAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    lastFailure = client.LastError;
                    await client.DisposeAsync().ConfigureAwait(false);
                    continue;
                }
                client.ToolsChanged          += () => OnServerToolsChanged(entry);
                client.Closed                += () => OnServerClosed(entry, client);
                client.AuthorizationRequired += () => OnServerSignInRefused(entry, client);
                var discovered = await client.ListToolsAsync(CancellationToken.None).ConfigureAwait(false);
                if (discovered is null)
                {
                    // Restarted but not listed: publishing it "connected" with no tools would end the retries.
                    Diagnostics.Record("Mcp",
                        $"Server '{entry.Config.Name}' restarted, but its tool list could not be read: {client.LastError}");
                    lastFailure = $"its tool list could not be read: {client.LastError}";
                    await client.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                // Disposed while this server was starting: the gate is gone, and a client nobody holds would leave
                // its process running after the editor — neither the teardown nor KillAllServers knows it.
                if (_disposed)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                    return;
                }
                try
                {
                    await _gate.WaitAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                    return;
                }
                try
                {
                    // The entry may have been torn down (settings save) while we were reconnecting.
                    if (_disposed || !_servers.Contains(entry))
                    {
                        await client.DisposeAsync().ConfigureAwait(false);
                        return;
                    }
                    entry.Client       = client;
                    entry.Tools        = BuildTools(client, discovered);
                    entry.Connected    = true;
                    entry.AuthRequired = false;
                    entry.Error        = null;
                    RebuildSnapshot();
                    return;
                }
                finally { _gate.Release(); }
            }

            // 3. Backoff exhausted — leave it disconnected until the next settings save.
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed || !_servers.Contains(entry)) return;
                entry.Error = string.IsNullOrWhiteSpace(lastFailure)
                    ? "server exited — reconnect failed"
                    : $"server exited — reconnect failed: {lastFailure}";
                RebuildSnapshot();
            }
            finally { _gate.Release(); }
        }
        finally
        {
            entry.EndReconnect();
            // The client just published died while this reconnect still held the slot: reconnect again
            // rather than leave it "connected" on a dead process. A close of an older client is stale.
            if (entry.TakeClosedDuringReconnect() is { } closed && ReferenceEquals(closed, entry.Client))
                OnServerClosed(entry, closed);
        }
    }

    /// <summary>
    /// A server that accepted the session and now refuses its sign-in (an expired or revoked token).
    /// </summary>
    /// <remarks>
    /// ⚠ Left "connected", its card offers no "Sign in" and every call of its tools fails with a 401 the model can do
    /// nothing about. Its tools are withdrawn (a call of one names the cause, <see cref="DescribeMissingTool"/>) and its
    /// client closed until the user signs in, which restarts the servers.
    /// </remarks>
    private void OnServerSignInRefused(ServerEntry entry, IMcpClient client) => _ = MarkSignInRefusedAsync(entry, client);

    private async Task MarkSignInRefusedAsync(ServerEntry entry, IMcpClient client)
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { return; }
        try
        {
            if (_disposed || !_servers.Contains(entry) || !ReferenceEquals(entry.Client, client)) return;
            if (client.ResourceMetadataUrl is { Length: > 0 } announced)
                _announcedMetadata[(entry.Config.Name, entry.Config.Url)] = announced;
            entry.Tools        = [];
            entry.Connected    = false;
            entry.AuthRequired = true;
            entry.Error        = "the server refused its sign-in during the session — sign in again";
            Diagnostics.Record("Mcp",
                $"Server '{entry.Config.Name}' refused its sign-in during the session (401): its tools are withdrawn until you sign in again.");
            try { await client.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Diagnostics.Swallow($"McpToolService.SignInRefused({entry.Config.Name})", ex); }
            RebuildSnapshot();
        }
        finally { _gate.Release(); }
    }

    private List<ITool> BuildTools(IMcpClient client, IReadOnlyList<McpToolInfo> infos) =>
        infos.Select(info => (ITool)new McpTool(client, info, _approval)).ToList();

    /// <summary>Recomputes the public <see cref="Tools"/>/<see cref="Status"/> snapshots from the
    /// current server entries. Must be called under <see cref="_gate"/>.</summary>
    private void RebuildSnapshot()
    {
        // ⚠ Server names are normalised into tool names: "my-server" and "my.server" both give
        // mcp__my_server__*. The backend received duplicate definitions and every call went to the first
        // server. A clash is exposed under a suffixed name, and said.
        var seen  = new HashSet<string>(StringComparer.Ordinal);
        var tools = new List<ITool>();
        foreach (var tool in _servers.SelectMany(e => e.Tools))
        {
            if (seen.Add(tool.Name)) { tools.Add(tool); continue; }
            var n = 2;
            string renamed;
            do renamed = $"{tool.Name}_{n++}"; while (!seen.Add(renamed));
            Diagnostics.Record("Mcp", $"Tool name '{tool.Name}' is used by two servers; the second is exposed as '{renamed}'.");
            tools.Add(tool is McpTool mcp ? mcp.WithName(renamed) : tool);
        }
        _snapshot = new Snapshot(tools,
        [
            .. _rejected,
            .. _failed,
            .. _servers.Select(e => new McpServerStatus(e.Config.Name, e.Connected, e.Tools.Count, e.Error, e.AuthRequired)),
        ]);
    }

    private async Task TeardownAsync()
    {
        var old = _servers;
        _servers  = [];
        _failed   = [];
        _rejected = [];
        _snapshot = _snapshot with { Tools = [] };
        foreach (var entry in old)
            await entry.Client.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Kills every server process without awaiting — for shutdown paths where blocking is
    /// forbidden. Leaves the managed state alone: the process is exiting anyway, and an orphaned
    /// MCP server (node, python, uvx…) outliving the editor is the failure this prevents.
    /// </summary>
    public void KillAllServers()
    {
        foreach (var entry in _servers)
        {
            try { entry.Client.KillNow(); }
            catch (Exception ex) { Diagnostics.Swallow($"McpToolService.KillAllServers({entry.Client.ServerName})", ex); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await TeardownAsync().ConfigureAwait(false); }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
