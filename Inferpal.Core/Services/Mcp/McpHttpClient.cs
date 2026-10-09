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
/// MCP client over the <b>Streamable HTTP</b> transport: JSON-RPC requests are POSTed to a single endpoint, and the
/// server replies either with a single <c>application/json</c> body or a <c>text/event-stream</c> (SSE) carrying the
/// response.
/// </summary>
/// <remarks>
/// <para>
/// Two eras. With a 2026-07-28 server — one that answers the <c>server/discover</c> the client opens with — there is no
/// session: every request carries its <c>_meta</c> and the request headers (<c>MCP-Protocol-Version</c>,
/// <c>Mcp-Method</c>, <c>Mcp-Name</c>, the tool's <c>Mcp-Param-*</c>), and tool-list changes arrive on a
/// <c>subscriptions/listen</c> stream. With an earlier server, the <c>initialize</c> handshake, the <c>Mcp-Session-Id</c>
/// it returns echoed on every later request, and the optional GET stream for list changes.
/// </para>
/// <para>
/// Auth is header-based: each configured header is sent on every request, with <c>${ENV_VAR}</c> placeholders expanded
/// from the environment at construction time (so tokens stay out of the stored config). An ended notification stream is
/// a normal rotation, not a disconnect, so <see cref="Closed"/> is never raised.
/// </para>
/// </remarks>
internal sealed partial class McpHttpClient : McpClientBase, IMcpClient
{
    private readonly McpServerConfig _config;
    private readonly Uri _url;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly OAuth.IMcpTokenProvider? _tokenProvider;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _listenCts = new();
    private static readonly TimeSpan ListenReopenDelay = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling of the exponential back-off between stream re-openings.</summary>
    private static readonly TimeSpan ListenReopenMaxDelay = TimeSpan.FromMinutes(2);
    private Task? _listenLoop;
    private long _nextId;
    private string? _sessionId;
    private volatile bool _disposed;

    /// <summary>The <c>x-mcp-header</c> parameters of each tool of the last listing (modern era only).</summary>
    private volatile IReadOnlyDictionary<string, IReadOnlyList<McpParamHeaders.Annotation>> _paramHeaders =
        new Dictionary<string, IReadOnlyList<McpParamHeaders.Annotation>>(StringComparer.Ordinal);

    public McpHttpClient(McpServerConfig config, HttpMessageHandler? handler = null,
                         OAuth.IMcpTokenProvider? tokenProvider = null)
    {
        _config = config;
        _url    = new Uri(config.Url!);
        _headers = (config.Headers ?? new Dictionary<string, string>())
            .ToDictionary(kv => kv.Key, kv => ExpandEnv(kv.Value), StringComparer.OrdinalIgnoreCase);
        _sendsOwnCredential = _headers.Keys.Any(IsCredentialHeader);
        _unsetVariables = (config.Headers ?? new Dictionary<string, string>())
            .SelectMany(kv => UnsetVariables(kv.Value).Select(v => $"header '{kv.Key}' uses ${{{v}}}"))
            .ToList();
        _tokenProvider = tokenProvider;
        // An injected handler is owned by the caller (tests); a default one is owned by this client.
        // Redirects are NOT followed: the configured headers carry secrets (`${ENV}` expansion is
        // documented for exactly that), and .NET only strips `Authorization` across origins — a
        // custom `X-Api-Key` would ride along to whatever host the server redirects to. Same
        // stance as FetchUrlTool, which validates every hop by hand.
        _http = handler is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            : new HttpClient(handler, disposeHandler: false);
        // Applies to the buffered JSON path (ReadAsStringAsync); the SSE path reads the stream
        // itself and is bounded by MaxSseEventChars below. Both exist for the same reason: the
        // body is written by the server, and "the user configured it" is not "the user vouches
        // for every byte it will ever send".
        _http.MaxResponseContentBufferSize = 32 * 1024 * 1024;
        // ⚠ No client-wide timeout: every call carries its own budget (20 s handshake, 120 s tool call),
        // and HttpClient's default 100 s cut a legitimate tool call short before its budget ran out.
        _http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
    }

    public override string ServerName => _config.Name;

    /// <summary>A configured header carries the user's own credential (an API key, a static token).</summary>
    private readonly bool _sendsOwnCredential;

    /// <summary>The <c>${VAR}</c> placeholders of the configured headers that the environment does not set.</summary>
    private readonly List<string> _unsetVariables;

    private static readonly HttpRequestOptionsKey<bool> OAuthTokenSent = new("inferpal.mcp.oauth-token");

    /// <summary>A header name that carries a credential: <c>Authorization</c>, or a name speaking of a key, token,
    /// secret or password.</summary>
    internal static bool IsCredentialHeader(string name) =>
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
        || new[] { "key", "token", "secret", "password", "auth" }.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a 401 asks for an OAuth sign-in: when the OAuth token went with the request, or when no credential of the
    /// user's own did.
    /// </summary>
    /// <remarks>⚠ A server authenticated by a configured API key answers a refused key with 401 too. Read as "sign in",
    /// the card showed <b>Needs sign-in</b> with the server's reason hidden, and <b>Sign in</b> ran an OAuth discovery the
    /// server does not offer — while the key, or the <c>${VAR}</c> it was read from, was what to fix.</remarks>
    internal static bool AsksForSignIn(bool oauthTokenSent, bool ownCredentialSent) => oauthTokenSent || !ownCredentialSent;

    /// <summary>Set when the server rejected the request with 401 and OAuth is configured — the user
    /// must (re-)authorize via the settings UI. Surfaced as a distinct connection status.</summary>
    public bool NeedsAuthorization { get; private set; }

    /// <inheritdoc/>
    public event Action? AuthorizationRequired;

    /// <inheritdoc/>
    public string? ResourceMetadataUrl { get; private set; }

    /// <summary>Raised when the notification stream (the GET stream, or <c>subscriptions/listen</c> in the modern era)
    /// delivers <c>tools/list_changed</c>.</summary>
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
            var discovery = await DiscoverAsync(cts.Token).ConfigureAwait(false);
            if (discovery.Failure is { } failure) throw new InvalidOperationException(failure);
            Era                    = discovery.Era;
            ServerToolsListChanged = discovery.ToolsListChanged;
            if (Era == McpEra.Legacy) await HandshakeAsync(cts.Token).ConfigureAwait(false);
            // Best-effort: listen on the server→client stream for tool-list changes.
            _listenLoop = Task.Run(() => ListenForNotificationsAsync(_listenCts.Token));
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// The first exchange, which decides the era: a modern request, <c>server/discover</c>.
    /// </summary>
    /// <remarks>
    /// A <c>DiscoverResult</c> or a recognised modern error is a modern server; anything else — a 400, 404 or 405 without
    /// one, <c>-32601</c>, a 2025 session error, a body that is not JSON — is a server of the earlier revisions, and the
    /// handshake follows (the binding's backward-compatibility rule). ⚠ A refused credential (401, 403) is not an era: it
    /// stops the start here, as the handshake's refusal did — the sign-in state is set on the way
    /// (<see cref="SendRequestAsync(string, JsonNode, CancellationToken, bool)"/>).
    /// </remarks>
    private async Task<McpDiscovery> DiscoverAsync(CancellationToken ct)
    {
        try
        {
            return McpModern.Classify(
                await SendRequestAsync("server/discover", McpModern.WithMeta(null), ct, allowReinit: false).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { throw; }
        catch (Exception ex) { return McpModern.FromError(ex); }
    }

    /// <summary>Performs the <c>initialize</c> → <c>notifications/initialized</c> handshake, starting a
    /// fresh session. Also used to re-establish a session that the server has expired (see 404 handling
    /// in <see cref="SendRequestAsync"/>).</summary>
    private async Task HandshakeAsync(CancellationToken ct)
    {
        var initParams = new JsonObject
        {
            ["protocolVersion"] = McpModern.LegacyVersion,
            ["capabilities"]    = new JsonObject(),
            ["clientInfo"]      = McpModern.ClientInfo(),
        };
        await SendRequestAsync("initialize", initParams, ct, allowReinit: false).ConfigureAwait(false);
        await SendNotificationAsync("notifications/initialized", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// ⚠ The client MUST leave out a tool whose <c>x-mcp-header</c> annotations break the rules, and send the headers of
    /// the others: a modern server refuses a call without them. Both are decided here, at the listing.
    /// </summary>
    private protected override IReadOnlyList<McpToolInfo> Admit(IReadOnlyList<McpToolInfo> tools)
    {
        if (Era != McpEra.Modern) return tools;
        var admitted = new List<McpToolInfo>(tools.Count);
        var headers  = new Dictionary<string, IReadOnlyList<McpParamHeaders.Annotation>>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            var (annotations, rejection) = McpParamHeaders.Read(tool.InputSchema);
            if (rejection is not null)
            {
                Diagnostics.RecordOnce("Mcp",
                    $"'{ServerName}' tool '{tool.Name}' is not offered: {rejection}. The MCP specification has the client " +
                    "leave out a tool whose x-mcp-header annotation is invalid.",
                    $"{ServerName}\u0001{tool.Name}\u0001{rejection}");
                continue;
            }
            if (annotations.Count > 0) headers[tool.Name] = annotations;
            admitted.Add(tool);
        }
        _paramHeaders = headers;
        return admitted;
    }



    // ── JSON-RPC over Streamable HTTP ─────────────────────────────────────────

    /// <summary>
    /// The base's transport hook. The HTTP transport needs one extra degree of freedom — whether a
    /// dropped session may be re-established mid-request — which is private to it and must not leak
    /// into the shared protocol layer.
    /// </summary>
    private protected override Task<JsonElement> SendRequestAsync(
        string method, JsonNode @params, CancellationToken ct) =>
        SendRequestAsync(method, @params, ct, allowReinit: true);

    private async Task<JsonElement> SendRequestAsync(string method, JsonNode @params, CancellationToken ct,
                                                     bool allowReinit)
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

        var modern = McpModern.IsModern(payload);
        using var resp = await PostAsync(payload, ct).ConfigureAwait(false);

        // A 404 on a request that carried a session id means the server expired it: start a fresh
        // session and replay the request once (allowReinit guards against looping). The modern era has no session.
        if (allowReinit && !modern && resp.StatusCode == HttpStatusCode.NotFound && _sessionId is not null)
        {
            _sessionId = null;
            await HandshakeAsync(ct).ConfigureAwait(false);
            return await SendRequestAsync(method, @params, ct, allowReinit: false).ConfigureAwait(false);
        }

        // 401 with OAuth configured ⇒ token absent/rejected; surface "authorize required" — unless the refused credential is
        // the user's own (see AsksForSignIn).
        if (resp.StatusCode == HttpStatusCode.Unauthorized && _tokenProvider is not null
            && AsksForSignIn(resp.RequestMessage?.Options.TryGetValue(OAuthTokenSent, out var sent) == true && sent,
                             _sendsOwnCredential)
            && !NeedsAuthorization)
        {
            NeedsAuthorization = true;
            // Said to whoever holds this client: in the middle of a session nothing else would read the flag.
            AuthorizationRequired?.Invoke();
        }
        // And where the sign-in must look: the server may announce its metadata address in the challenge.
        if (resp.StatusCode == HttpStatusCode.Unauthorized
            && resp.Headers.TryGetValues("WWW-Authenticate", out var challenges)
            && OAuth.McpOAuthMetadata.ParseResourceMetadataUrl(string.Join(", ", challenges)) is { } announced)
            ResourceMetadataUrl = announced;

        if (!modern) CaptureSession(resp);
        // ⚠ A placeholder the environment does not set was sent as an empty value: on a refusal, it is the first suspect.
        var unset = resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && _unsetVariables.Count > 0
            ? $"{string.Join(", ", _unsetVariables)}, not set in this editor's environment — sent empty"
            : null;
        await ThrowIfRefusedAsync(resp, ct, unset).ConfigureAwait(false);
        return await ReadResultAsync(resp, id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// ⚠ A refusal carries its reason in the BODY — "invalid API key", or a JSON-RPC error such as the official SDK's
    /// "No valid session ID provided" — and <c>EnsureSuccessStatusCode</c> keeps only the status line, which names
    /// nothing a user can act on. The status stays on the exception for whoever branches on it.
    /// </summary>
    private static async Task ThrowIfRefusedAsync(HttpResponseMessage resp, CancellationToken ct, string? note = null)
    {
        if (resp.IsSuccessStatusCode) return;

        var body = string.Empty;
        try { body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Diagnostics.Swallow("McpHttpClient.RefusalBody", ex); }
        var detail = RefusalDetail(body);
        var (code, data) = RefusalRpcError(body);

        var head = $"HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase ?? resp.StatusCode.ToString()})";
        var said = detail.Length == 0 ? head : $"{head}: {detail}";
        // The JSON-RPC code travels with the refusal: a modern server refuses with 400 and a code that names the era.
        throw new McpHttpRefusedException(note is null ? said : $"{said} ({note})", resp.StatusCode, code, data, detail);
    }

    /// <summary>The <c>code</c> and <c>data</c> of the JSON-RPC error a refusal's body carries, when it carries one.</summary>
    private static (long? Code, JsonElement? Data) RefusalRpcError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body.Trim());
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                return (McpJsonRpc.ErrorCode(error),
                        error.TryGetProperty("data", out var data) ? data.Clone() : null);
        }
        catch (JsonException) { /* not a JSON-RPC body */ }
        return (null, null);
    }

    /// <summary>
    /// The reason in a refusal's body: a JSON-RPC <c>error.message</c>, an <c>error</c> or <c>message</c> string, or
    /// short plain text. An HTML page (a proxy's error screen) says nothing the status line does not.
    /// </summary>
    internal static string RefusalDetail(string body)
    {
        body = body.Trim();
        if (body.Length == 0 || body[0] == '<') return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                // The JSON-RPC error member when there is one, else the body itself ({"message": …}).
                var text = McpJsonRpc.ErrorMessage(root.TryGetProperty("error", out var error) ? error : root);
                if (text != McpJsonRpc.UnknownError) return text;
            }
        }
        catch (JsonException) { /* plain text */ }
        return body.Length > 300 ? body[..300] + "…" : body;
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
        using var req = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        // Streamable HTTP requires the client to accept both response shapes.
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (McpModern.IsModern(payload))
            AddModernHeaders(req, payload);
        else if (_sessionId is not null)
            req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        await ApplyAuthHeadersAsync(req, ct).ConfigureAwait(false);

        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        // The 401 reading asks what went WITH the request (AsksForSignIn); not every handler links the two.
        resp.RequestMessage ??= req;
        return resp;
    }

    /// <summary>
    /// The request headers of the modern era, mirrored from the body: the version, the method, the name a call targets,
    /// and the tool's <c>x-mcp-header</c> parameters — a server validates each against the body and refuses a call
    /// whose headers are missing.
    /// </summary>
    private void AddModernHeaders(HttpRequestMessage req, JsonNode payload)
    {
        req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", McpModern.Version);
        var method = payload["method"] is JsonValue m && m.TryGetValue<string>(out var s) ? s : null;
        if (method is null) return;
        req.Headers.TryAddWithoutValidation("Mcp-Method", method);

        var @params = payload["params"] as JsonObject;
        var target  = method is "tools/call" or "prompts/get" ? @params?["name"] : method == "resources/read" ? @params?["uri"] : null;
        if (target is not JsonValue t || !t.TryGetValue<string>(out var name)) return;
        req.Headers.TryAddWithoutValidation("Mcp-Name", McpParamHeaders.Encode(name));

        if (method == "tools/call" && _paramHeaders.TryGetValue(name, out var annotations))
            foreach (var a in annotations)
                if (McpParamHeaders.ValueFor(@params?["arguments"], a.Path) is { } value)
                    req.Headers.TryAddWithoutValidation($"Mcp-Param-{a.Name}", McpParamHeaders.Encode(value));
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
            req.Options.Set(OAuthTokenSent, true);
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
        // A server request (ping) can carry the same id as our call: it is not the response. And an id
        // sent as a string is still ours (McpJsonRpc.TryReadId — TryGetInt64 threw on it).
        if (McpJsonRpc.IsServerMessage(msg) || !McpJsonRpc.TryReadId(msg, out var mid) || mid != id)
            return false;

        if (msg.TryGetProperty("error", out var error))
            throw new McpRpcException(error);   // code and data kept: the era decision reads them

        result = msg.TryGetProperty("result", out var r) ? r.Clone() : McpJsonRpc.EmptyObject();
        return true;
    }

    // ── Server→client notification stream ────────────────────────────────────

    /// <summary>Opens the server's notification stream — the GET stream of the earlier revisions, a
    /// <c>subscriptions/listen</c> in the modern era — and raises <see cref="ToolsChanged"/> on each
    /// <c>tools/list_changed</c>. A cleanly ended stream is re-opened after a pause (the server may
    /// rotate it); a non-stream response or any error stops listening for good. Never throws.</summary>
    private async Task ListenForNotificationsAsync(CancellationToken ct)
    {
        // ⚠ A modern server sends a list change only on a subscription, and only one it announced: without the
        // subscription its new tools stay unseen until a reconnect; without the announcement there is nothing to ask for.
        if (Era == McpEra.Modern && !ServerToolsListChanged) return;

        // Back-off between re-openings. A server that accepts the GET and closes the stream at once
        // would otherwise be hammered once a second for the whole session, forever; a healthy
        // stream that ran for a while resets the delay.
        var delay = ListenReopenDelay;

        while (!ct.IsCancellationRequested && !_disposed)
        {
            HttpResponseMessage resp;
            try
            {
                resp = Era == McpEra.Modern
                    ? await OpenSubscriptionAsync(ct).ConfigureAwait(false)
                    : await OpenGetStreamAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                return;   // cancelled or network error — stop listening
            }

            var isStream = resp.IsSuccessStatusCode
                && string.Equals(resp.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);
            if (!isStream)
            {
                // A server that announced list changes and refuses the subscription is worth a word; one without a GET
                // stream is the ordinary case of the earlier revisions.
                if (Era == McpEra.Modern)
                    Diagnostics.RecordOnce("Mcp",
                        $"'{ServerName}' announced tool-list changes but refused the subscription (HTTP {(int)resp.StatusCode}); " +
                        "a later change to its tools is seen on the next connection.", ServerName);
                resp.Dispose();
                return;
            }

            var openedAt = DateTime.UtcNow;
            try
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await foreach (var data in ReadSseEventsAsync(stream, ct).ConfigureAwait(false))
                    if (IsToolsListChanged(data))
                        ToolsChanged?.Invoke();
            }
            catch { /* stream dropped — fall through to reopen */ }
            finally { resp.Dispose(); }

            // A stream that lived long enough to be useful resets the back-off; one that dies
            // immediately doubles it, up to the ceiling.
            delay = DateTime.UtcNow - openedAt >= ListenReopenMaxDelay
                ? ListenReopenDelay
                : TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, ListenReopenMaxDelay.Ticks));

            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch { return; }
        }
    }

    private async Task<HttpResponseMessage> OpenGetStreamAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (_sessionId is not null)
            req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        await ApplyAuthHeadersAsync(req, ct).ConfigureAwait(false);
        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    /// <summary>A <c>subscriptions/listen</c> for tool-list changes: its response is the long-lived stream.</summary>
    private Task<HttpResponseMessage> OpenSubscriptionAsync(CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"]      = Interlocked.Increment(ref _nextId),
            ["method"]  = "subscriptions/listen",
            ["params"]  = McpModern.WithMeta(new JsonObject { ["notifications"] = new JsonObject { ["toolsListChanged"] = true } }),
        };
        return PostAsync(payload, ct);
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

    /// <summary>Ceiling on one accumulated SSE event; past it the event is dropped, not buffered.</summary>
    private const int MaxSseEventChars = 32 * 1024 * 1024;

    /// <summary>Yields the <c>data</c> payload of each SSE event from <paramref name="stream"/> (multiple
    /// <c>data:</c> lines joined by newline); non-data fields and comments are ignored.</summary>
    private static async IAsyncEnumerable<string> ReadSseEventsAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        // ⚠ The cap was declared and never applied: data lines without a blank separator grew this buffer
        // without limit in the host process. Past it the event is dropped, up to its end.
        var dropping = false;
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0 && !dropping) yield return data.ToString();
                data.Clear();
                dropping = false;
                continue;
            }
            if (dropping) continue;
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length + line.Length > MaxSseEventChars)
                {
                    Diagnostics.Record("Mcp", $"An event stream message over {MaxSseEventChars / (1024 * 1024)} MB was dropped.");
                    data.Clear();
                    dropping = true;
                    continue;
                }
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).Trim());
            }
            // event:, id:, retry:, and ": " comment lines are not needed here.
        }
        if (data.Length > 0) yield return data.ToString();   // trailing event with no blank-line terminator
    }

    /// <summary>The variables named by <c>${VAR}</c> placeholders of <paramref name="value"/> that the environment does not
    /// set (or sets empty).</summary>
    internal static IEnumerable<string> UnsetVariables(string value) =>
        EnvPlaceholder().Matches(value).Select(m => m.Groups[1].Value)
            .Where(v => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v))).Distinct();

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
