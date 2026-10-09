using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Inferpal.Services.Mcp;

/// <summary>
/// Minimal MCP client over the stdio transport — spawns a server process and speaks
/// newline-delimited JSON-RPC 2.0 on its stdin/stdout. Zero external dependencies.
/// </summary>
/// <remarks>
/// Speaks both eras: the 2026-07-28 revision (no handshake, <c>_meta</c> on every request) when the server answers the
/// <c>server/discover</c> probe, the <c>initialize</c> → <c>notifications/initialized</c> handshake otherwise — then
/// <c>tools/list</c> and <c>tools/call</c>, enough to surface MCP tools to the agent.
/// </remarks>
internal sealed class McpStdioClient : McpClientBase, IMcpClient
{
    /// <summary>
    /// How long the server may stay silent on the discovery probe before the handshake is sent too.
    /// </summary>
    /// <remarks>
    /// A server of the earlier revisions may ignore anything before its <c>initialize</c> — the binding's "does not respond
    /// within a reasonable timeout". This is not a verdict: a slow server reads its input in order, so its late answer to
    /// the probe still decides, and the handshake budget stays the one it was.
    /// </remarks>
    private static readonly TimeSpan ProbeWait = TimeSpan.FromSeconds(3);

    private readonly McpServerConfig _config;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _listenCts = new();

    private Process? _process;
    private Task?    _readLoop;
    private Task?    _stderrDrain;
    private long     _nextId;
    private bool     _started;
    /// <summary>The handshake completed: from then on, a close is a death mid-session.</summary>
    private volatile bool _ready;
    private volatile bool _disposed;

    // What the server writes on stderr, kept at its edges: the only place it says why it cannot start.
    private readonly Shell.StderrEdges _stderr = new();

    /// <summary>How long a failed start waits for the process to finish exiting, for its code and last words.</summary>
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(2);

    public McpStdioClient(McpServerConfig config) => _config = config;

    /// <summary>The server name this client is bound to (for tool namespacing and diagnostics).</summary>
    public override string ServerName => _config.Name;

    /// <summary>Last connection error, if <see cref="StartAsync"/> returned <c>false</c>.</summary>

    /// <summary>stdio servers never use OAuth (credentials come from the environment).</summary>
    public bool NeedsAuthorization => false;

    /// <summary>
    /// Raised when the server sends a <c>notifications/tools/list_changed</c> notification, signalling
    /// that its advertised tool set has changed. Fires on the background read-loop thread; handlers
    /// should not block. The owner re-runs <see cref="ListToolsAsync"/> in response.
    /// </summary>
    public event Action? ToolsChanged;

    /// <summary>
    /// Raised once when the connection drops unexpectedly (the server process exited or closed its
    /// stdout) — i.e. not as a result of <see cref="DisposeAsync"/>. Fires on the read-loop thread;
    /// the owner uses it to drop the dead server's tools and attempt a reconnect.
    /// </summary>
    public event Action? Closed;

    /// <summary>
    /// Spawns the process and performs the MCP handshake. Returns <c>false</c> (never throws)
    /// when the server cannot be launched or the handshake fails.
    /// </summary>
    public async Task<bool> StartAsync(CancellationToken ct)
    {
        try
        {
            // `"command": "npx"` — most server READMEs — is a batch script on Windows: run by node, never by cmd.exe.
            var shim = Shell.NodeShim.Resolve(_config.Command ?? string.Empty, OperatingSystem.IsWindows(),
                                              Shell.ShellLauncher.FindOnPath, File.Exists);
            var psi = new ProcessStartInfo
            {
                FileName               = shim?.FileName ?? _config.Command,
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                StandardInputEncoding  = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            foreach (var arg in shim?.Prefix ?? [])
                psi.ArgumentList.Add(arg);
            foreach (var arg in _config.Args)
                psi.ArgumentList.Add(arg);
            foreach (var kv in _config.Env)
                psi.Environment[kv.Key] = kv.Value;
            // A repository's server runs in the repository (RepoMcpServers); the user's own keep the editor's folder.
            if (!string.IsNullOrEmpty(_config.WorkingDirectory))
                psi.WorkingDirectory = _config.WorkingDirectory;

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!_process.Start())
            {
                LastError = "Process failed to start.";
                return false;
            }
            _started = true;
            // A server stops when its stdin closes; the children it started (npx → node) do not.
            ProcessLifetime.Bind(_process);

            // Drain stderr so a chatty server never blocks on a full pipe — keeping its edges for a failed start.
            var stderr = _process.StandardError;
            _stderrDrain = Task.Run(() => _stderr.DrainAsync(stderr));

            _readLoop = Task.Run(() => ReadLoopAsync());

            // ── Era, and the handshake when it is the earlier one ────────────
            using var initCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            initCts.CancelAfter(HandshakeTimeout);
            var discovery = await OpenAsync(initCts.Token).ConfigureAwait(false);
            Era                    = discovery.Era;
            ServerToolsListChanged = discovery.ToolsListChanged;
            _ready = true;
            // ⚠ A modern server sends list_changed only on a subscription asked for: without one, a server whose tools
            // change would keep its old tools here until a reconnect.
            if (Era == McpEra.Modern && ServerToolsListChanged)
                _ = ListenForToolChangesAsync();
            return true;
        }
        catch (Exception ex)
        {
            LastError = await DescribeStartFailureAsync(ex, ct).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// Decides the era by the probe the stdio binding prescribes — <c>server/discover</c> first — and completes the
    /// legacy handshake when that is the answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>DiscoverResult</c> or a recognised modern error is a modern server (<see cref="McpModern"/>); any other error
    /// is a server of the earlier revisions, and <c>initialize</c> follows. A server that stays silent past
    /// <see cref="ProbeWait"/> is sent <c>initialize</c> as well, and whichever answer comes first decides: a merely slow
    /// server answers the probe first, since it reads in order; a silent one answers only the handshake.
    /// </para>
    /// <para>
    /// ⚠ The handshake's version is unchanged (<see cref="McpModern.LegacyVersion"/>): a server that works today keeps
    /// receiving exactly what it received, preceded by one request it refuses.
    /// </para>
    /// </remarks>
    private async Task<McpDiscovery> OpenAsync(CancellationToken ct)
    {
        var discover = SendRequestAsync("server/discover", McpModern.WithMeta(null), ct);
        Task<JsonElement>? initialize = null;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            if (await Task.WhenAny(discover, Task.Delay(ProbeWait, wait.Token)).ConfigureAwait(false) != discover)
                initialize = SendRequestAsync("initialize", InitializeParams(), ct);
            await wait.CancelAsync().ConfigureAwait(false);
        }

        while (!discover.IsCompleted)
        {
            // Silent so far, and the handshake sent: whichever answers first.
            var first = await Task.WhenAny(discover, initialize!).ConfigureAwait(false);
            if (first == initialize && initialize.IsCompletedSuccessfully)
            {
                Observe(discover);
                await SendNotificationAsync("notifications/initialized").ConfigureAwait(false);
                return McpDiscovery.Legacy;
            }
            // The handshake refused: a modern server refuses it — and answers the probe. Wait for that answer.
            if (first == initialize)
                try { await discover.ConfigureAwait(false); } catch (Exception) { /* decided below */ }
        }

        var decided = Decide(discover);
        if (decided.Era == McpEra.Modern || decided.Failure is not null)
        {
            Observe(initialize);
            return decided.Failure is { } failure ? throw new InvalidOperationException(failure) : decided;
        }

        initialize ??= SendRequestAsync("initialize", InitializeParams(), ct);
        await initialize.ConfigureAwait(false);
        await SendNotificationAsync("notifications/initialized").ConfigureAwait(false);
        return McpDiscovery.Legacy;
    }

    /// <summary>The era the probe's outcome names: its answer, its refusal, or — no answer at all — the earlier one.</summary>
    private static McpDiscovery Decide(Task<JsonElement> probe) =>
        probe.Status switch
        {
            TaskStatus.RanToCompletion => McpModern.Classify(probe.Result),
            TaskStatus.Faulted         => McpModern.FromError(probe.Exception!.InnerException ?? probe.Exception),
            _                          => McpDiscovery.Legacy,
        };

    /// <summary>The losing request of the race is not awaited: its failure is observed, never raised later.</summary>
    private static void Observe(Task? task) =>
        task?.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                           TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static JsonObject InitializeParams() => new()
    {
        ["protocolVersion"] = McpModern.LegacyVersion,
        ["capabilities"]    = new JsonObject(),
        ["clientInfo"]      = McpModern.ClientInfo(),
    };

    /// <summary>
    /// The subscription a modern server delivers <c>notifications/tools/list_changed</c> on, kept for the session: the
    /// request stays open, and the notifications reach <see cref="Dispatch"/> like any other.
    /// </summary>
    private async Task ListenForToolChangesAsync()
    {
        try
        {
            var subscribe = new JsonObject { ["notifications"] = new JsonObject { ["toolsListChanged"] = true } };
            await SendRequestAsync("subscriptions/listen", McpModern.WithMeta(subscribe), _listenCts.Token).ConfigureAwait(false);
            if (!_disposed)
                Diagnostics.Record("Mcp", $"'{ServerName}' ended its tools/list_changed subscription; a later change to its tools is seen on the next connection.");
        }
        catch (OperationCanceledException) { /* disposed */ }
        catch (Exception ex)
        {
            if (!_disposed)
                Diagnostics.Record("Mcp", $"'{ServerName}' refused the tools/list_changed subscription ({ex.Message}); a later change to its tools is seen on the next connection.");
        }
    }

    /// <summary>
    /// Why the start failed, in the terms that fix it: a process that exited is named with its exit code and what it
    /// wrote on stderr; a live one that never answered is named with the budget it had.
    /// </summary>
    private async Task<string> DescribeStartFailureAsync(Exception ex, CancellationToken ct)
    {
        // A process that never started has no exit to wait for — asking throws. Its message names the command.
        if (ct.IsCancellationRequested || !_started || _process is null) return ex.Message;

        return await DescribeExitAsync("before answering").ConfigureAwait(false)
            ?? (ex is OperationCanceledException
                ? $"initialize got no answer within {HandshakeTimeout.TotalSeconds:0} s"
                : ex.Message);
    }

    /// <summary>
    /// The server's exit, named with its code and what it wrote on stderr; <c>null</c> while the process still runs.
    /// </summary>
    private async Task<string?> DescribeExitAsync(string when)
    {
        if (_process is not { } p) return null;

        // The pipe closes a moment before the process is reaped: wait for its code.
        using (var grace = new CancellationTokenSource(ExitGrace))
        {
            try { await p.WaitForExitAsync(grace.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        if (!p.HasExited) return null;

        // Its last lines may still be in the pipe when stdout closes.
        if (_stderrDrain is { } drain)
        {
            try { await drain.WaitAsync(ExitGrace).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        var said = _stderr.ToString();
        return said.Length == 0
            ? $"the server exited with code {p.ExitCode} {when} (nothing on stderr)"
            : $"the server exited with code {p.ExitCode} {when}; stderr: {said}";
    }

    /// <summary>Lists the tools the server advertises; <c>null</c> when the listing failed.</summary>

    /// <summary>
    /// Calls a tool by its server-local name and returns the concatenated text content.
    /// </summary>

    // ── JSON-RPC plumbing ────────────────────────────────────────────────────

    private protected override async Task<JsonElement> SendRequestAsync(
        string method, JsonNode @params, CancellationToken ct)
    {
        if (_disposed || _process is null || _process.HasExited)
            throw new InvalidOperationException($"MCP server '{_config.Name}' is not running.");

        var id  = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"]      = id,
            ["method"]  = method,
            ["params"]  = @params,
        };

        try
        {
            await WriteLineAsync(request.ToJsonString(), ct).ConfigureAwait(false);
            // ⚠ On stdio a request goes on running in the server unless it is told: closing a stream is HTTP's signal,
            // not this binding's — and the modern era requires the notification.
            using (ct.Register(() =>
                   {
                       if (tcs.TrySetCanceled(ct) && Era == McpEra.Modern && !_disposed) _ = SendCancelledAsync(id);
                   }))
                return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendCancelledAsync(long id)
    {
        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"]  = "notifications/cancelled",
            ["params"]  = new JsonObject { ["requestId"] = id },
        };
        try { await WriteLineAsync(notification.ToJsonString(), CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { Diagnostics.Swallow($"McpStdioClient.Cancel({_config.Name})", ex); }
    }

    private Task SendNotificationAsync(string method)
    {
        var notification = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        return WriteLineAsync(notification.ToJsonString(), CancellationToken.None);
    }

    private async Task WriteLineAsync(string json, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _process!.StandardInput.WriteAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.WriteAsync("\n".AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            string? line;
            while ((line = await _process!.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0) continue;
                Dispatch(line);
            }
        }
        catch { /* stdout closed — server exited */ }
        finally
        {
            FailAllPending(new InvalidOperationException($"MCP server '{_config.Name}' connection closed."));
            // Only an *unexpected* close is a reconnect signal; an intentional Dispose sets _disposed first.
            if (!_disposed)
            {
                // ⚠ A server that dies MID-SESSION is restarted without a word unless its exit is named here: the start
                // path names a failed start's code and stderr, and a server crashing on every call left no trace at all.
                // Only after the handshake — before it, StartAsync describes the failure itself.
                if (_ready)
                    LastError = await DescribeExitAsync("mid-session").ConfigureAwait(false) ?? "the server closed its output";
                Closed?.Invoke();
            }
        }
    }

    private void Dispatch(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            // What the server initiated carries a "method": a notification (we act on the tool list
            // changing) or a REQUEST, which carries an id too. ⚠ A request (ping, elicitation…) was
            // matched against our pending calls and resolved one with an empty result — and, never
            // answered, could make the server drop the connection.
            if (McpJsonRpc.IsServerMessage(root))
            {
                var method = root.TryGetProperty("method", out var methodEl) && methodEl.ValueKind == JsonValueKind.String
                    ? methodEl.GetString()
                    : null;
                if (method == "notifications/tools/list_changed")
                    ToolsChanged?.Invoke();
                if (root.TryGetProperty("id", out var requestId))
                    _ = AnswerServerRequestAsync(requestId.Clone(), method);
                return;
            }
            // Some servers echo the numeric id back as a STRING ("42"); McpJsonRpc.TryReadId reads both.
            if (!McpJsonRpc.TryReadId(root, out var id) || !_pending.TryGetValue(id, out var tcs))
                return;

            if (root.TryGetProperty("error", out var error))
                tcs.TrySetException(new McpRpcException(error));   // code and data kept: the era decision reads them
            else if (root.TryGetProperty("result", out var result))
            {
                tcs.TrySetResult(result.Clone());
            }
            else
            {
                tcs.TrySetResult(McpJsonRpc.EmptyObject());
            }
        }
        catch (JsonException) { /* skip non-JSON noise on stdout */ }
    }

    /// <summary>Answers a request the server sent us: <c>ping</c> gets its empty result, anything else a
    /// "method not found" error — never silence, which a server may read as a dead client.</summary>
    private async Task AnswerServerRequestAsync(JsonElement id, string? method)
    {
        var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id.GetRawText()) };
        if (method == "ping")
            response["result"] = new JsonObject();
        else
            response["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"Method not supported by this client: {method}" };
        try { await WriteLineAsync(response.ToJsonString(), CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { Diagnostics.Swallow($"McpStdioClient.AnswerServerRequest({_config.Name})", ex); }
    }

    private void FailAllPending(Exception ex)
    {
        foreach (var kv in _pending)
            kv.Value.TrySetException(ex);
        _pending.Clear();
    }

    /// <summary>Synchronous kill for shutdown paths that cannot await (see <see cref="IMcpClient.KillNow"/>).</summary>
    public void KillNow()
    {
        _disposed = true;
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) { Diagnostics.Swallow($"McpStdioClient.KillNow({ServerName})", ex); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _listenCts.CancelAsync().ConfigureAwait(false);
        FailAllPending(new ObjectDisposedException(nameof(McpStdioClient)));

        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }

        if (_readLoop is not null)
            // Awaiting our own background read loop to drain it before disposing the process.
#pragma warning disable VSTHRD003 // intentional: _readLoop is started by this instance in StartAsync
            try { await _readLoop.ConfigureAwait(false); } catch { }
#pragma warning restore VSTHRD003

        _process?.Dispose();
        _writeLock.Dispose();
        _listenCts.Dispose();
    }
}
