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
/// Implements only the handshake (<c>initialize</c> → <c>notifications/initialized</c>) plus
/// <c>tools/list</c> and <c>tools/call</c> — enough to surface MCP tools to the agent.
/// HTTP/SSE transport is intentionally out of scope for v1.
/// </remarks>
internal sealed class McpStdioClient : IMcpClient
{
    private const string ProtocolVersion = "2024-11-05";
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CallTimeout      = TimeSpan.FromSeconds(120);

    private readonly McpServerConfig _config;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private Process? _process;
    private Task?    _readLoop;
    private long     _nextId;
    private volatile bool _disposed;

    public McpStdioClient(McpServerConfig config) => _config = config;

    /// <summary>The server name this client is bound to (for tool namespacing and diagnostics).</summary>
    public string ServerName => _config.Name;

    /// <summary>Last connection error, if <see cref="StartAsync"/> returned <c>false</c>.</summary>
    public string? LastError { get; private set; }

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
            var psi = new ProcessStartInfo
            {
                FileName               = _config.Command,
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                StandardInputEncoding  = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            foreach (var arg in _config.Args)
                psi.ArgumentList.Add(arg);
            foreach (var kv in _config.Env)
                psi.Environment[kv.Key] = kv.Value;

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!_process.Start())
            {
                LastError = "Process failed to start.";
                return false;
            }

            // Drain stderr so a chatty server never blocks on a full pipe.
            _ = Task.Run(async () =>
            {
                try { while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is not null) { } }
                catch { /* process exited */ }
            });

            _readLoop = Task.Run(() => ReadLoopAsync());

            // ── MCP handshake ────────────────────────────────────────────────
            var initParams = new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"]    = new JsonObject(),
                ["clientInfo"]      = new JsonObject { ["name"] = "Inferpal", ["version"] = "1.0" },
            };
            using var initCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            initCts.CancelAfter(HandshakeTimeout);
            await SendRequestAsync("initialize", initParams, initCts.Token).ConfigureAwait(false);

            await SendNotificationAsync("notifications/initialized").ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>Lists the tools the server advertises. Returns an empty list on failure.</summary>
    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(HandshakeTimeout);
            var result = await SendRequestAsync("tools/list", new JsonObject(), cts.Token).ConfigureAwait(false);
            return McpJsonRpc.ParseTools(result);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Calls a tool by its server-local name and returns the concatenated text content.
    /// </summary>
    public async Task<string> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct)
    {
        var callParams = new JsonObject { ["name"] = toolName };
        callParams["arguments"] = arguments.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? JsonNode.Parse(arguments.GetRawText())
            : new JsonObject();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallTimeout);
        var result = await SendRequestAsync("tools/call", callParams, cts.Token).ConfigureAwait(false);
        return McpJsonRpc.ExtractCallResult(result, toolName);
    }

    // ── JSON-RPC plumbing ────────────────────────────────────────────────────

    private async Task<JsonElement> SendRequestAsync(string method, JsonNode @params, CancellationToken ct)
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
            using (ct.Register(() => tcs.TrySetCanceled(ct)))
                return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
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
                Closed?.Invoke();
        }
    }

    private void Dispatch(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            // Responses carry an "id"; server-initiated notifications do not. We act on the one
            // notification we care about (tool list changed) and ignore the rest.
            if (!root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id))
            {
                if (root.TryGetProperty("method", out var methodEl)
                    && methodEl.ValueKind == JsonValueKind.String
                    && methodEl.GetString() == "notifications/tools/list_changed")
                    ToolsChanged?.Invoke();
                return;
            }
            if (!_pending.TryGetValue(id, out var tcs))
                return;

            if (root.TryGetProperty("error", out var error))
            {
                var msg = error.TryGetProperty("message", out var m) ? m.GetString() : "unknown error";
                tcs.TrySetException(new InvalidOperationException($"MCP error: {msg}"));
            }
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

    private void FailAllPending(Exception ex)
    {
        foreach (var kv in _pending)
            kv.Value.TrySetException(ex);
        _pending.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

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
    }
}
