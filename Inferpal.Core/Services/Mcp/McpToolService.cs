using Inferpal.Config;
using Inferpal.Services.Mcp.OAuth;
using Inferpal.Services.Tools;

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
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Per-server state. Mutated only under <see cref="_gate"/> (except the reconnect guard,
    /// which is lock-free so the read-loop thread can claim it without blocking).</summary>
    private sealed class ServerEntry(McpServerConfig config, IMcpClient client)
    {
        public McpServerConfig Config { get; } = config;
        public IMcpClient Client { get; set; } = client;
        public IReadOnlyList<ITool> Tools { get; set; } = [];
        public bool Connected { get; set; } = true;
        public string? Error { get; set; }

        private int _reconnecting;
        /// <summary>Claims the single in-flight reconnect slot; returns false if one is already running.</summary>
        public bool TryBeginReconnect() => Interlocked.CompareExchange(ref _reconnecting, 1, 0) == 0;
        public void EndReconnect() => Interlocked.Exchange(ref _reconnecting, 0);
    }

    private List<ServerEntry> _servers = [];
    private IReadOnlyList<McpServerStatus> _failed = [];   // servers that could not be started at all
    private volatile IReadOnlyList<ITool> _tools = [];
    private volatile IReadOnlyList<McpServerStatus> _status = [];
    private bool _disposed;

    public McpToolService(InferpalConfig config, IApprovalService approval)
        : this(config, approval, clientFactory: null) { }

    /// <summary>Picks the transport from the server entry: a <c>url</c> ⇒ Streamable HTTP (with an OAuth
    /// token provider bound to the encrypted store), otherwise stdio.</summary>
    private IMcpClient DefaultClientFactory(McpServerConfig cfg) =>
        cfg.IsHttp ? new McpHttpClient(cfg, tokenProvider: TokenProviderFor(cfg)) : new McpStdioClient(cfg);

    private McpStoredTokenProvider TokenProviderFor(McpServerConfig cfg) =>
        new(cfg.Name, _tokenStore, new McpOAuthFlow(new RefreshOnlyReceiver()));

    /// <summary>Test seam: injects the client transport (production uses the transport factory above)
    /// and, optionally, a faster reconnect-backoff schedule and token store.</summary>
    internal McpToolService(InferpalConfig config, IApprovalService approval,
                            Func<McpServerConfig, IMcpClient>? clientFactory,
                            IReadOnlyList<TimeSpan>? reconnectBackoff = null,
                            McpTokenStore? tokenStore = null)
    {
        _config           = config;
        _approval         = approval;
        _clientFactory    = clientFactory ?? DefaultClientFactory;
        _reconnectBackoff = reconnectBackoff ?? DefaultReconnectBackoff;
        _tokenStore       = tokenStore ?? new McpTokenStore(McpTokenStore.DefaultPath);
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
    public IReadOnlyList<ITool> Tools => _tools;

    /// <summary>Per-server connection status, for display in the settings window.</summary>
    public IReadOnlyList<McpServerStatus> Status => _status;

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
                _tools  = [];
                _status = [];
                return;
            }

            var servers = McpServerConfig.Parse(_config.McpServersJson);
            var entries = new List<ServerEntry>();
            var failed  = new List<McpServerStatus>();

            foreach (var server in servers.Where(s => s.Enabled))
            {
                var client = _clientFactory(server);
                var ok     = await client.StartAsync(CancellationToken.None).ConfigureAwait(false);
                if (!ok)
                {
                    failed.Add(new McpServerStatus(server.Name, false, 0, client.LastError, client.NeedsAuthorization));
                    await client.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                // Wire lifecycle events before discovery so a death mid-listing still triggers reconnect.
                var entry = new ServerEntry(server, client);
                client.ToolsChanged += () => OnServerToolsChanged(entry);
                client.Closed       += () => OnServerClosed(entry);

                entry.Tools = BuildTools(client, await client.ListToolsAsync(CancellationToken.None).ConfigureAwait(false));
                entries.Add(entry);
            }

            _servers = entries;
            _failed  = failed;
            RebuildSnapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs the interactive OAuth authorization (browser) for one HTTP server, persists the resulting
    /// tokens to the encrypted store, then reconnects so its tools load. Throws if the server isn't a
    /// configured HTTP server or the user cancels/denies. Called from the settings "Authorize" action.
    /// </summary>
    public async Task AuthorizeAsync(string serverName, CancellationToken ct = default)
    {
        var cfg = McpServerConfig.Parse(_config.McpServersJson)
            .FirstOrDefault(s => s.IsHttp && string.Equals(s.Name, serverName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"'{serverName}' is not a configured HTTP MCP server.");

        var server = new McpOAuthServer(
            new Uri(cfg.Url!), cfg.OAuth?.ClientId, cfg.OAuth?.ClientSecret, cfg.OAuth?.Scopes);

        var flow  = new McpOAuthFlow(new LoopbackAuthCodeReceiver());
        var state = await flow.AuthorizeAsync(server, _tokenStore.Get(serverName), ct).ConfigureAwait(false);
        _tokenStore.Save(serverName, state);

        await RefreshAsync().ConfigureAwait(false);
    }

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
            entry.Tools = BuildTools(client, await client.ListToolsAsync(CancellationToken.None).ConfigureAwait(false));
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
    private void OnServerClosed(ServerEntry entry)
    {
        if (_disposed) return;
        if (entry.TryBeginReconnect())
            _ = ReconnectAsync(entry);
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
                await entry.Client.DisposeAsync().ConfigureAwait(false);
                entry.Tools     = [];
                entry.Connected = false;
                entry.Error     = "server exited — reconnecting…";
                RebuildSnapshot();
            }
            finally { _gate.Release(); }

            // 2. Retry with backoff, outside the gate so saves and other servers aren't blocked.
            foreach (var delay in _reconnectBackoff)
            {
                await Task.Delay(delay).ConfigureAwait(false);
                if (_disposed || !_servers.Contains(entry)) return;

                var client = _clientFactory(entry.Config);
                if (!await client.StartAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                    continue;
                }
                client.ToolsChanged += () => OnServerToolsChanged(entry);
                client.Closed       += () => OnServerClosed(entry);
                var discovered = await client.ListToolsAsync(CancellationToken.None).ConfigureAwait(false);

                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    // The entry may have been torn down (settings save) while we were reconnecting.
                    if (_disposed || !_servers.Contains(entry))
                    {
                        await client.DisposeAsync().ConfigureAwait(false);
                        return;
                    }
                    entry.Client    = client;
                    entry.Tools     = BuildTools(client, discovered);
                    entry.Connected = true;
                    entry.Error     = null;
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
                entry.Error = "server exited — reconnect failed";
                RebuildSnapshot();
            }
            finally { _gate.Release(); }
        }
        finally
        {
            entry.EndReconnect();
        }
    }

    private List<ITool> BuildTools(IMcpClient client, IReadOnlyList<McpToolInfo> infos) =>
        infos.Select(info => (ITool)new McpTool(client, info, _approval)).ToList();

    /// <summary>Recomputes the public <see cref="Tools"/>/<see cref="Status"/> snapshots from the
    /// current server entries. Must be called under <see cref="_gate"/>.</summary>
    private void RebuildSnapshot()
    {
        _tools  = _servers.SelectMany(e => e.Tools).ToList();
        _status =
        [
            .. _failed,
            .. _servers.Select(e => new McpServerStatus(e.Config.Name, e.Connected, e.Tools.Count, e.Error)),
        ];
    }

    private async Task TeardownAsync()
    {
        var old = _servers;
        _servers = [];
        _failed  = [];
        _tools   = [];
        foreach (var entry in old)
            await entry.Client.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await TeardownAsync().ConfigureAwait(false); }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
