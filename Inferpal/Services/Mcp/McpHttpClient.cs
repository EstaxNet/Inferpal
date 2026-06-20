using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Mcp;

/// <summary>
/// MCP client over the <b>Streamable HTTP</b> transport (spec 2025-03-26): JSON-RPC requests are
/// POSTed to a single endpoint, and the server replies either with a single <c>application/json</c>
/// body or a <c>text/event-stream</c> (SSE) carrying the response. The optional <c>Mcp-Session-Id</c>
/// header returned by <c>initialize</c> is echoed on every subsequent request.
/// </summary>
/// <remarks>
/// Auth is header-based: each configured header is sent on every request, with <c>${ENV_VAR}</c>
/// placeholders expanded from the environment at construction time (so tokens stay out of the stored
/// config). After the handshake the client opens the optional server→client GET stream and raises
/// <see cref="ToolsChanged"/> on a <c>tools/list_changed</c> notification (live re-discovery). An ended
/// GET stream is a normal rotation, not a disconnect, so <see cref="Closed"/> is never raised — HTTP
/// session-expiry reconnect and interactive OAuth are out of scope for this cut.
/// </remarks>
internal sealed partial class McpHttpClient : IMcpClient
{
    private const string ProtocolVersion = "2024-11-05";
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CallTimeout      = TimeSpan.FromSeconds(120);

    private readonly McpServerConfig _config;
    private readonly Uri _url;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly OAuth.IMcpTokenProvider? _tokenProvider;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _listenCts = new();
    private static readonly TimeSpan ListenReopenDelay = TimeSpan.FromSeconds(1);
    private Task? _listenLoop;
    private long _nextId;
    private string? _sessionId;
    private volatile bool _disposed;

    public McpHttpClient(McpServerConfig config, HttpMessageHandler? handler = null,
                         OAuth.IMcpTokenProvider? tokenProvider = null)
    {
        _config = config;
        _url    = new Uri(config.Url!);
        _headers = (config.Headers ?? new Dictionary<string, string>())
            .ToDictionary(kv => kv.Key, kv => ExpandEnv(kv.Value), StringComparer.OrdinalIgnoreCase);
        _tokenProvider = tokenProvider;
        // An injected handler is owned by the caller (tests); a default one is owned by this client.
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
    }

    public string ServerName => _config.Name;
    public string? LastError { get; private set; }

    /// <summary>Set when the server rejected the request with 401 and OAuth is configured — the user
    /// must (re-)authorize via the settings UI. Surfaced as a distinct connection status.</summary>
    public bool NeedsAuthorization { get; private set; }

    /// <summary>Raised when the server's GET notification stream delivers <c>tools/list_changed</c>.</summary>
    public event Action? ToolsChanged;

    // HTTP has no process-death signal: an ended GET stream is a normal rotation, not a disconnect,
    // so Closed is never raised. Accessors kept only to satisfy IMcpClient.
    public event Action? Closed { add { } remove { } }

    public async Task<bool> StartAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(HandshakeTimeout);
            await HandshakeAsync(cts.Token).ConfigureAwait(false);
            // Best-effort: listen on the optional server→client stream for tool-list changes.
            _listenLoop = Task.Run(() => ListenForNotificationsAsync(_listenCts.Token));
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>Performs the <c>initialize</c> → <c>notifications/initialized</c> handshake, starting a
    /// fresh session. Also used to re-establish a session that the server has expired (see 404 handling
    /// in <see cref="SendRequestAsync"/>).</summary>
    private async Task HandshakeAsync(CancellationToken ct)
    {
        var initParams = new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"]    = new JsonObject(),
            ["clientInfo"]      = new JsonObject { ["name"] = "Inferpal", ["version"] = "1.0" },
        };
        await SendRequestAsync("initialize", initParams, ct, allowReinit: false).ConfigureAwait(false);
        await SendNotificationAsync("notifications/initialized", ct).ConfigureAwait(false);
    }

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

    // ── JSON-RPC over Streamable HTTP ─────────────────────────────────────────

    private async Task<JsonElement> SendRequestAsync(string method, JsonNode @params, CancellationToken ct,
                                                     bool allowReinit = true)
    {
        if (_disposed)
            throw new InvalidOperationException($"MCP server '{_config.Name}' client is disposed.");

        var id = Interlocked.Increment(ref _nextId);
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"]      = id,
            ["method"]  = method,
            // Clone so a JsonNode argument isn't re-parented (it would throw if the request is replayed
            // after a session re-init, since a node can belong to only one parent).
            ["params"]  = @params.DeepClone(),
        };

        var resp = await PostAsync(payload, ct).ConfigureAwait(false);
        try
        {
            // A 404 on a request that carried a session id means the server expired it: start a fresh
            // session and replay the request once (allowReinit guards against looping).
            if (allowReinit && resp.StatusCode == HttpStatusCode.NotFound && _sessionId is not null)
            {
                _sessionId = null;
                await HandshakeAsync(ct).ConfigureAwait(false);
                return await SendRequestAsync(method, @params, ct, allowReinit: false).ConfigureAwait(false);
            }

            // 401 with OAuth configured ⇒ token absent/rejected; surface "authorize required".
            if (resp.StatusCode == HttpStatusCode.Unauthorized && _tokenProvider is not null)
                NeedsAuthorization = true;

            CaptureSession(resp);
            resp.EnsureSuccessStatusCode();
            return await ReadResultAsync(resp, id, ct).ConfigureAwait(false);
        }
        finally
        {
            resp.Dispose();
        }
    }

    private async Task SendNotificationAsync(string method, CancellationToken ct)
    {
        var payload = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        using var resp = await PostAsync(payload, ct).ConfigureAwait(false);
        CaptureSession(resp);
        // Notifications get 202 Accepted with no body — nothing to read.
    }

    private async Task<HttpResponseMessage> PostAsync(JsonNode payload, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        // Streamable HTTP requires the client to accept both response shapes.
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (_sessionId is not null)
            req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        await ApplyAuthHeadersAsync(req, ct).ConfigureAwait(false);

        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    /// <summary>Adds the configured static headers, then overlays an OAuth <c>Bearer</c> token from the
    /// token provider (if any) on the <c>Authorization</c> header.</summary>
    private async Task ApplyAuthHeadersAsync(HttpRequestMessage req, CancellationToken ct)
    {
        foreach (var h in _headers)
            req.Headers.TryAddWithoutValidation(h.Key, h.Value);

        if (_tokenProvider is not null
            && await _tokenProvider.GetAccessTokenAsync(ct).ConfigureAwait(false) is { Length: > 0 } token)
        {
            req.Headers.Remove("Authorization");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }
    }

    private void CaptureSession(HttpResponseMessage resp)
    {
        if (resp.Headers.TryGetValues("Mcp-Session-Id", out var values))
        {
            var id = values.FirstOrDefault();
            if (!string.IsNullOrEmpty(id)) _sessionId = id;
        }
    }

    /// <summary>Reads the JSON-RPC response for <paramref name="id"/> from either a single JSON body or
    /// an SSE stream, and unwraps its <c>result</c> (throwing on a JSON-RPC <c>error</c>).</summary>
    private static async Task<JsonElement> ReadResultAsync(HttpResponseMessage resp, long id, CancellationToken ct)
    {
        var mediaType = resp.Content.Headers.ContentType?.MediaType;

        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await foreach (var data in ReadSseEventsAsync(stream, ct).ConfigureAwait(false))
                if (TryMatchMessage(data, id, out var result)) return result;
            throw new InvalidOperationException("MCP HTTP: event stream ended without a matching response.");
        }

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var msg in root.EnumerateArray())
                if (TryExtract(msg, id, out var result)) return result;
            throw new InvalidOperationException("MCP HTTP: response batch had no matching id.");
        }
        if (TryExtract(root, id, out var single)) return single;
        throw new InvalidOperationException("MCP HTTP: response id did not match the request.");
    }

    private static bool TryMatchMessage(string data, long id, out JsonElement result)
    {
        result = default;
        if (data.Length == 0) return false;
        try
        {
            using var doc = JsonDocument.Parse(data);
            return TryExtract(doc.RootElement, id, out result);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Returns true and sets <paramref name="result"/> when the message is the response for
    /// <paramref name="id"/>; throws on a JSON-RPC error; returns false for any other message.</summary>
    private static bool TryExtract(JsonElement msg, long id, out JsonElement result)
    {
        result = default;
        if (!msg.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var mid) || mid != id)
            return false;

        if (msg.TryGetProperty("error", out var error))
        {
            var m = error.TryGetProperty("message", out var mm) ? mm.GetString() : "unknown error";
            throw new InvalidOperationException($"MCP error: {m}");
        }

        result = msg.TryGetProperty("result", out var r) ? r.Clone() : McpJsonRpc.EmptyObject();
        return true;
    }

    // ── Server→client notification stream (optional GET) ──────────────────────

    /// <summary>Opens the server's GET SSE stream and raises <see cref="ToolsChanged"/> on each
    /// <c>tools/list_changed</c>. A cleanly ended stream is re-opened after a pause (the server may
    /// rotate it); a non-stream response or any error stops listening for good. Never throws.</summary>
    private async Task ListenForNotificationsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            HttpResponseMessage resp;
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, _url);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                if (_sessionId is not null)
                    req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
                await ApplyAuthHeadersAsync(req, ct).ConfigureAwait(false);
                resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch
            {
                return;   // cancelled or network error — stop listening
            }

            var isStream = resp.IsSuccessStatusCode
                && string.Equals(resp.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);
            if (!isStream)
            {
                resp.Dispose();
                return;   // the server doesn't offer a notification stream
            }

            try
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await foreach (var data in ReadSseEventsAsync(stream, ct).ConfigureAwait(false))
                    if (IsToolsListChanged(data))
                        ToolsChanged?.Invoke();
            }
            catch { /* stream dropped — fall through to reopen */ }
            finally { resp.Dispose(); }

            try { await Task.Delay(ListenReopenDelay, ct).ConfigureAwait(false); }
            catch { return; }
        }
    }

    private static bool IsToolsListChanged(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            return doc.RootElement.TryGetProperty("method", out var m)
                && m.ValueKind == JsonValueKind.String
                && m.GetString() == "notifications/tools/list_changed";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Yields the <c>data</c> payload of each SSE event from <paramref name="stream"/> (multiple
    /// <c>data:</c> lines joined by newline); non-data fields and comments are ignored.</summary>
    private static async IAsyncEnumerable<string> ReadSseEventsAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0) { yield return data.ToString(); data.Clear(); }
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).Trim());
            }
            // event:, id:, retry:, and ": " comment lines are not needed here.
        }
        if (data.Length > 0) yield return data.ToString();   // trailing event with no blank-line terminator
    }

    /// <summary>Replaces <c>${VAR}</c> placeholders with the matching environment variable (empty if unset).</summary>
    internal static string ExpandEnv(string value) =>
        EnvPlaceholder().Replace(value, m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? string.Empty);

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex EnvPlaceholder();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _listenCts.CancelAsync().ConfigureAwait(false);
        if (_listenLoop is not null)
            // Awaiting our own listener loop (started in StartAsync) to drain it before disposing.
#pragma warning disable VSTHRD003 // intentional: _listenLoop is started by this instance
            try { await _listenLoop.ConfigureAwait(false); } catch { /* listener cancelled */ }
#pragma warning restore VSTHRD003
        _http.Dispose();
        _listenCts.Dispose();
    }
}
