using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Inferpal.Services.Mcp.OAuth;

/// <summary>Inputs for an authorization run: the MCP server plus any pre-configured client credentials
/// and scopes, and an optional resource-metadata URL discovered from a <c>WWW-Authenticate</c> header.</summary>
internal sealed record McpOAuthServer(
    Uri Url,
    string? ClientId = null,
    string? ClientSecret = null,
    IReadOnlyList<string>? Scopes = null,
    string? ResourceMetadataUrl = null);

/// <summary>
/// Runs the MCP OAuth 2.1 authorization-code flow (spec 2025-06-18): discovers the authorization server
/// (RFC 9728 → RFC 8414, with default-endpoint fallback), registers a client if needed (RFC 7591),
/// drives the PKCE browser flow via <see cref="IAuthCodeReceiver"/>, and exchanges/refreshes tokens —
/// always carrying the RFC 8707 <c>resource</c> parameter. HTTP is via an injectable handler for tests.
/// </summary>
internal sealed class McpOAuthFlow
{
    private static readonly string[] GrantTypes = ["authorization_code", "refresh_token"];

    private readonly HttpClient _http;
    private readonly IAuthCodeReceiver _receiver;

    public McpOAuthFlow(IAuthCodeReceiver receiver, HttpMessageHandler? handler = null)
    {
        _receiver = receiver;
        _http     = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
    }

    /// <summary>Performs the full interactive authorization and returns the resulting OAuth state
    /// (client credentials + tokens). Reuses a previously registered client id when present.</summary>
    public async Task<McpOAuthState> AuthorizeAsync(McpOAuthServer server, McpOAuthState? existing, CancellationToken ct)
    {
        var resource = McpOAuthMetadata.CanonicalResource(server.Url);
        var asm      = await DiscoverAsync(server, ct).ConfigureAwait(false);
        var scopes   = ResolveScopes(server, asm);

        var (clientId, clientSecret) = await EnsureClientAsync(server, existing, asm, ct).ConfigureAwait(false);

        var (verifier, challenge) = Pkce.Create();
        var state = Pkce.NewState();
        var authUrl = BuildAuthorizationUrl(asm.AuthorizationEndpoint, clientId, challenge, state, scopes, resource);

        var (code, returnedState) = await _receiver.GetAuthorizationCodeAsync(authUrl, ct).ConfigureAwait(false);
        if (!string.Equals(returnedState, state, StringComparison.Ordinal))
            throw new InvalidOperationException("OAuth state mismatch — possible CSRF; aborting.");

        var form = new Dictionary<string, string>
        {
            ["grant_type"]    = "authorization_code",
            ["code"]          = code,
            ["redirect_uri"]  = _receiver.RedirectUri,
            ["client_id"]     = clientId,
            ["code_verifier"] = verifier,
            ["resource"]      = resource,
        };
        if (!string.IsNullOrEmpty(clientSecret)) form["client_secret"] = clientSecret!;

        var token = await PostTokenAsync(asm.TokenEndpoint, form, ct).ConfigureAwait(false);
        return BuildState(token, clientId, clientSecret, asm.TokenEndpoint, resource, scopes);
    }

    /// <summary>Exchanges the stored refresh token for a fresh access token. Returns the updated state,
    /// or null when there is no refresh token or the server rejects it.</summary>
    public async Task<McpOAuthState?> RefreshAsync(McpOAuthState state, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(state.RefreshToken) || string.IsNullOrEmpty(state.TokenEndpoint)
            || string.IsNullOrEmpty(state.ClientId))
            return null;

        var form = new Dictionary<string, string>
        {
            ["grant_type"]    = "refresh_token",
            ["refresh_token"] = state.RefreshToken!,
            ["client_id"]     = state.ClientId!,
        };
        if (!string.IsNullOrEmpty(state.Resource))     form["resource"]      = state.Resource!;
        if (!string.IsNullOrEmpty(state.ClientSecret)) form["client_secret"] = state.ClientSecret!;

        try
        {
            var token = await PostTokenAsync(state.TokenEndpoint!, form, ct).ConfigureAwait(false);
            // A refresh response may omit a new refresh token — keep the old one then.
            return BuildState(token, state.ClientId!, state.ClientSecret, state.TokenEndpoint!, state.Resource,
                              state.Scopes, fallbackRefreshToken: state.RefreshToken);
        }
        catch
        {
            return null;
        }
    }

    // ── Discovery ─────────────────────────────────────────────────────────────

    private async Task<AuthServerMetadata> DiscoverAsync(McpOAuthServer server, CancellationToken ct)
    {
        // 1) Protected Resource Metadata → authorization server URL (fall back to the server origin).
        Uri authServer = new(new Uri(McpOAuthMetadata.DefaultProtectedResourceMetadataUrl(server.Url)), "/");
        var prmUrl = server.ResourceMetadataUrl ?? McpOAuthMetadata.DefaultProtectedResourceMetadataUrl(server.Url);
        if (await TryGetAsync(prmUrl, ct).ConfigureAwait(false) is { } prmJson)
        {
            var prm = McpOAuthMetadata.ParseProtectedResourceMetadata(prmJson);
            if (prm.AuthorizationServers.Count > 0)
                authServer = new Uri(prm.AuthorizationServers[0]);
        }

        // 2) Authorization Server Metadata → endpoints (fall back to conventional default paths).
        var asmUrl = McpOAuthMetadata.DefaultAuthServerMetadataUrl(authServer);
        if (await TryGetAsync(asmUrl, ct).ConfigureAwait(false) is { } asmJson)
            return McpOAuthMetadata.ParseAuthServerMetadata(asmJson);

        return McpOAuthMetadata.DefaultEndpoints(authServer);
    }

    private async Task<(string ClientId, string? ClientSecret)> EnsureClientAsync(
        McpOAuthServer server, McpOAuthState? existing, AuthServerMetadata asm, CancellationToken ct)
    {
        // Reuse a previously registered/configured client where possible.
        if (!string.IsNullOrEmpty(existing?.ClientId)) return (existing!.ClientId!, existing.ClientSecret);
        if (!string.IsNullOrEmpty(server.ClientId))    return (server.ClientId!, server.ClientSecret);

        if (string.IsNullOrEmpty(asm.RegistrationEndpoint))
            throw new InvalidOperationException(
                "No client_id configured and the authorization server offers no dynamic registration.");

        return await RegisterClientAsync(asm.RegistrationEndpoint!, ct).ConfigureAwait(false);
    }

    private async Task<(string ClientId, string? ClientSecret)> RegisterClientAsync(string endpoint, CancellationToken ct)
    {
        var body = new
        {
            client_name              = "Inferpal",
            redirect_uris            = new[] { _receiver.RedirectUri },
            grant_types              = GrantTypes,
            response_types           = new[] { "code" },
            token_endpoint_auth_method = "none",
        };
        using var content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(endpoint, content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        var clientId = root.TryGetProperty("client_id", out var c) ? c.GetString() : null;
        if (string.IsNullOrEmpty(clientId))
            throw new InvalidOperationException("Dynamic client registration returned no client_id.");
        var secret = root.TryGetProperty("client_secret", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString() : null;
        return (clientId!, secret);
    }

    // ── Token endpoint ────────────────────────────────────────────────────────

    private async Task<TokenResponse> PostTokenAsync(string endpoint, Dictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var resp = await _http.PostAsync(endpoint, content, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token endpoint returned {(int)resp.StatusCode}: {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var access = root.TryGetProperty("access_token", out var a) ? a.GetString() : null;
        if (string.IsNullOrEmpty(access))
            throw new InvalidOperationException("Token response contained no access_token.");

        int? expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var ei) ? ei : null;
        var refresh = root.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString() : null;
        return new TokenResponse(access!, refresh, expiresIn);
    }

    private async Task<string?> TryGetAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-06-18");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
        }
        catch
        {
            return null;
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────--

    private static IReadOnlyList<string> ResolveScopes(McpOAuthServer server, AuthServerMetadata asm) =>
        server.Scopes is { Count: > 0 } s ? s : asm.ScopesSupported;

    private static string BuildAuthorizationUrl(string endpoint, string clientId, string challenge,
                                                string state, IReadOnlyList<string> scopes, string resource)
    {
        var q = new List<string>
        {
            "response_type=code",
            $"client_id={Uri.EscapeDataString(clientId)}",
            $"code_challenge={Uri.EscapeDataString(challenge)}",
            $"code_challenge_method={Pkce.Method}",
            $"state={Uri.EscapeDataString(state)}",
            $"resource={Uri.EscapeDataString(resource)}",
        };
        if (scopes.Count > 0) q.Add($"scope={Uri.EscapeDataString(string.Join(' ', scopes))}");
        var sep = endpoint.Contains('?') ? '&' : '?';
        return endpoint + sep + string.Join('&', q);
    }

    private static McpOAuthState BuildState(TokenResponse token, string clientId, string? clientSecret,
                                            string tokenEndpoint, string? resource, IReadOnlyList<string>? scopes,
                                            string? fallbackRefreshToken = null) => new()
    {
        ClientId      = clientId,
        ClientSecret  = clientSecret,
        AccessToken   = token.AccessToken,
        RefreshToken  = token.RefreshToken ?? fallbackRefreshToken,
        ExpiresAtUtc  = token.ExpiresIn is { } secs ? DateTimeOffset.UtcNow.AddSeconds(secs) : null,
        TokenEndpoint = tokenEndpoint,
        Resource      = resource,
        Scopes        = scopes?.ToList(),
    };

    private sealed record TokenResponse(string AccessToken, string? RefreshToken, int? ExpiresIn);
}
