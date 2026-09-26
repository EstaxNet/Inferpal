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
internal sealed class McpStdioClient : McpClientBase, IMcpClient
{
    private const string ProtocolVersion = "2024-11-05";

    private readonly McpServerConfig _config;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private Process? _process;
    private Task?    _readLoop;
    private Task?    _stderrDrain;
    private long     _nextId;
    private bool     _started;
    private volatile bool _disposed;

    // ⚠ stderr is where a server writes WHY it cannot start — a missing token, a bad path, a package npm cannot
    // find — and the only place: drained and discarded, a server that died at startup read "connection closed".
    // Node and npm put the reason on the FIRST line (the stack follows), Python on the LAST (after "Traceback"):
    // the first lines and the last ones are kept, bounded, since a healthy server may log for hours.
    private const int StderrEdgeLines = 4;
    private const int StderrLineChars = 200;
    private readonly object _stderrLock = new();
    private readonly List<string> _stderrHead = [];
    private readonly Queue<string> _stderrTail = new();
    private int _stderrLines;

    /// <summary>How long a failed start waits for the process to finish exiting, for its code and last words.</summary>
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(2);

    public McpStdioClient(McpServerConfig config) => _config = config;

    /// <summary>The server name this client is bound to (for tool namespacing and diagnostics).</summary>
    public string ServerName => _config.Name;

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

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!_process.Start())
            {
                LastError = "Process failed to start.";
                return false;
            }
            _started = true;

            // Drain stderr so a chatty server never blocks on a full pipe — keeping its edges for a failed start.
            var stderr = _process.StandardError;
            _stderrDrain = Task.Run(async () =>
            {
                try
                {
                    while (await stderr.ReadLineAsync().ConfigureAwait(false) is { } line)
                        KeepStderrLine(line);
                }
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
            LastError = await DescribeStartFailureAsync(ex, ct).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// Why the start failed, in the terms that fix it: a process that exited is named with its exit code and what it
    /// wrote on stderr; a live one that never answered is named with the budget it had.
    /// </summary>
    private async Task<string> DescribeStartFailureAsync(Exception ex, CancellationToken ct)
    {
        // A process that never started has no exit to wait for — asking throws. Its message names the command.
        if (ct.IsCancellationRequested || !_started || _process is not { } p) return ex.Message;

        // The handshake fails on the closed pipe a moment before the process is reaped: wait for its code.
        using (var grace = new CancellationTokenSource(ExitGrace))
        {
            try { await p.WaitForExitAsync(grace.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        if (!p.HasExited)
            return ex is OperationCanceledException
                ? $"initialize got no answer within {HandshakeTimeout.TotalSeconds:0} s"
                : ex.Message;

        // Its last lines may still be in the pipe when stdout closes.
        if (_stderrDrain is { } drain)
        {
            try { await drain.WaitAsync(ExitGrace).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        var said = StderrEdges();
        return said.Length == 0
            ? $"the server exited with code {p.ExitCode} before answering (nothing on stderr)"
            : $"the server exited with code {p.ExitCode} before answering; stderr: {said}";
    }

    private void KeepStderrLine(string line)
    {
        line = line.Trim();
        if (line.Length == 0) return;
        if (line.Length > StderrLineChars) line = line[..StderrLineChars] + "…";
        lock (_stderrLock)
        {
            _stderrLines++;
            if (_stderrHead.Count < StderrEdgeLines) { _stderrHead.Add(line); return; }
            _stderrTail.Enqueue(line);
            if (_stderrTail.Count > StderrEdgeLines) _stderrTail.Dequeue();
        }
    }

    /// <summary>The first and last lines of stderr on one line, the elided middle counted.</summary>
    private string StderrEdges()
    {
        lock (_stderrLock)
        {
            var skipped = _stderrLines - _stderrHead.Count - _stderrTail.Count;
            var parts   = new List<string>(_stderrHead);
            if (skipped > 0) parts.Add($"… {skipped} more line(s) …");
            parts.AddRange(_stderrTail);
            return string.Join(" | ", parts);
        }
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
            {
                var msg = McpJsonRpc.ErrorMessage(error);
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
