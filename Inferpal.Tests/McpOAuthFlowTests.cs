using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Inferpal.Services.Mcp.OAuth;
using Xunit;

namespace Inferpal.Tests;

public class McpOAuthFlowTests
{
    private static readonly Uri Server = new("https://mcp.example.com/mcp");

    private const string AsmWithRegistration = """
    {
      "issuer": "https://auth.example.com",
      "authorization_endpoint": "https://auth.example.com/authorize",
      "token_endpoint": "https://auth.example.com/token",
      "registration_endpoint": "https://auth.example.com/register",
      "scopes_supported": ["mcp.read"]
    }
    """;

    private const string AsmNoRegistration = """
    {
      "authorization_endpoint": "https://auth.example.com/authorize",
      "token_endpoint": "https://auth.example.com/token"
    }
    """;

    private sealed class FakeReceiver : IAuthCodeReceiver
    {
        public string RedirectUri => "http://127.0.0.1:9999/callback";
        public string Code { get; set; } = "auth-code-xyz";
        public bool ReturnWrongState { get; set; }
        public string? LastAuthUrl { get; private set; }

        /// <summary>The <c>iss</c> the redirect carries; <c>null</c> for one that names no issuer.</summary>
        public string? Iss { get; set; }
        public string? Error { get; set; }

        public Task<AuthorizationResponse> GetAuthorizationCodeAsync(string authorizationUrl, CancellationToken ct)
        {
            LastAuthUrl = authorizationUrl;
            var m = Regex.Match(authorizationUrl, @"[?&]state=([^&]+)");
            var state = ReturnWrongState ? "WRONG" : (m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : "");
            return Task.FromResult(new AuthorizationResponse(Error is null ? Code : null, state, Iss, Error,
                                                             Error is null ? null : "the user said no"));
        }
    }

    private sealed class FlowHandler : HttpMessageHandler
    {
        public List<(string Method, string Url, string Body)> Calls { get; } = [];
        public bool IncludeRegistration { get; set; } = true;
        /// <summary>The metadata says the server names itself in every authorization response (RFC 9207 §2.3).</summary>
        public bool IssAdvertised { get; set; }
        public string TokenJson { get; set; } = """{ "access_token":"AT", "refresh_token":"RT", "expires_in":3600 }""";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url  = request.RequestUri!.AbsoluteUri;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method.Method, url, body));

            if (request.Method == HttpMethod.Get && url.Contains("oauth-protected-resource"))
                return Json("""{ "resource":"https://mcp.example.com", "authorization_servers":["https://auth.example.com"] }""");
            if (request.Method == HttpMethod.Get && url.Contains("oauth-authorization-server"))
                return Json(!IncludeRegistration ? AsmNoRegistration
                    : IssAdvertised ? AsmWithRegistration.Replace("\"scopes_supported\"", "\"authorization_response_iss_parameter_supported\": true, \"scopes_supported\"", StringComparison.Ordinal)
                    : AsmWithRegistration);
            if (request.Method == HttpMethod.Post && url.EndsWith("/register"))
                return Json("""{ "client_id":"dcr-client-123" }""");
            if (request.Method == HttpMethod.Post && url.EndsWith("/token"))
                return Json(TokenJson);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static string TokenBody(FlowHandler h) => h.Calls.Last(c => c.Url.EndsWith("/token")).Body;

    // ── Authorize ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AuthorizeAsync_FullFlow_WithDynamicRegistration()
    {
        var handler  = new FlowHandler();
        var receiver = new FakeReceiver();
        var flow = new McpOAuthFlow(receiver, handler);

        var state = await flow.AuthorizeAsync(new McpOAuthServer(Server), existing: null, CancellationToken.None);

        Assert.Equal("AT", state.AccessToken);
        Assert.Equal("RT", state.RefreshToken);
        Assert.Equal("dcr-client-123", state.ClientId);
        Assert.Equal("https://auth.example.com/token", state.TokenEndpoint);
        Assert.NotNull(state.ExpiresAtUtc);

        // Authorization URL carries PKCE challenge, the canonical resource, and a state.
        Assert.Contains("code_challenge=", receiver.LastAuthUrl);
        Assert.Contains("code_challenge_method=S256", receiver.LastAuthUrl);
        Assert.Contains("resource=https%3A%2F%2Fmcp.example.com%2Fmcp", receiver.LastAuthUrl);

        // Token request is an authorization_code grant carrying the verifier + resource.
        var body = TokenBody(handler);
        Assert.Contains("grant_type=authorization_code", body);
        Assert.Contains("code_verifier=", body);
        Assert.Contains("code=auth-code-xyz", body);
        Assert.Contains("resource=", body);
    }

    [Fact]
    public async Task AuthorizeAsync_ConfiguredClientId_SkipsRegistration()
    {
        var handler = new FlowHandler();
        var flow = new McpOAuthFlow(new FakeReceiver(), handler);

        var state = await flow.AuthorizeAsync(
            new McpOAuthServer(Server, ClientId: "cfg-client"), existing: null, CancellationToken.None);

        Assert.Equal("cfg-client", state.ClientId);
        Assert.DoesNotContain(handler.Calls, c => c.Url.EndsWith("/register"));
    }

    [Fact]
    public async Task AuthorizeAsync_ReusesExistingClientId()
    {
        var handler = new FlowHandler();
        var flow = new McpOAuthFlow(new FakeReceiver(), handler);
        var existing = new McpOAuthState { ClientId = "prev-client", ClientSecret = "sek", Resource = "https://mcp.example.com/mcp" };

        var state = await flow.AuthorizeAsync(new McpOAuthServer(Server), existing, CancellationToken.None);

        Assert.Equal("prev-client", state.ClientId);
        Assert.DoesNotContain(handler.Calls, c => c.Url.EndsWith("/register"));
    }

    /// <summary>
    /// ⚠ A client id registered with ANOTHER server's authorization server (the name now points elsewhere) was offered
    /// to this one: a sign-in that could never succeed, until the user renamed the server or deleted the store by hand.
    /// </summary>
    [Fact]
    public async Task AuthorizeAsync_AStateFromAnotherServer_LendsNoClientId()
    {
        var handler  = new FlowHandler();
        var flow     = new McpOAuthFlow(new FakeReceiver(), handler);
        var existing = new McpOAuthState { ClientId = "client-of-a", Resource = "https://a.example.com/mcp" };

        var state = await flow.AuthorizeAsync(new McpOAuthServer(Server), existing, CancellationToken.None);

        Assert.Equal("dcr-client-123", state.ClientId);
        Assert.Contains(handler.Calls, c => c.Url.EndsWith("/register"));
    }

    /// <summary>A client id the user configured wins over one registered earlier: written to fix a sign-in, it was
    /// ignored as long as a stored one existed.</summary>
    [Fact]
    public async Task AuthorizeAsync_AConfiguredClientId_WinsOverAStoredOne()
    {
        var flow     = new McpOAuthFlow(new FakeReceiver(), new FlowHandler());
        var existing = new McpOAuthState { ClientId = "prev-client", Resource = "https://mcp.example.com/mcp" };

        var state = await flow.AuthorizeAsync(new McpOAuthServer(Server, ClientId: "cfg-client"), existing, CancellationToken.None);

        Assert.Equal("cfg-client", state.ClientId);
    }

    [Fact]
    public async Task AuthorizeAsync_StateMismatch_Throws()
    {
        var flow = new McpOAuthFlow(new FakeReceiver { ReturnWrongState = true }, new FlowHandler());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => flow.AuthorizeAsync(new McpOAuthServer(Server), null, CancellationToken.None));
    }

    [Fact]
    public async Task AuthorizeAsync_NoClientIdAndNoRegistration_Throws()
    {
        var handler = new FlowHandler { IncludeRegistration = false };
        var flow = new McpOAuthFlow(new FakeReceiver(), handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => flow.AuthorizeAsync(new McpOAuthServer(Server), null, CancellationToken.None));
        Assert.Contains("registration", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshAsync_ExchangesRefreshToken()
    {
        var handler = new FlowHandler();
        var flow = new McpOAuthFlow(new FakeReceiver(), handler);
        var state = new McpOAuthState
        {
            ClientId = "c", RefreshToken = "old-rt", TokenEndpoint = "https://auth.example.com/token",
            Resource = "https://mcp.example.com/mcp",
        };

        var refreshed = await flow.RefreshAsync(state, CancellationToken.None);

        Assert.NotNull(refreshed);
        Assert.Equal("AT", refreshed!.AccessToken);
        Assert.Contains("grant_type=refresh_token", TokenBody(handler));
    }

    [Fact]
    public async Task RefreshAsync_KeepsOldRefreshToken_WhenResponseOmitsOne()
    {
        var handler = new FlowHandler { TokenJson = """{ "access_token":"AT2", "expires_in":60 }""" };
        var flow = new McpOAuthFlow(new FakeReceiver(), handler);
        var state = new McpOAuthState
        {
            ClientId = "c", RefreshToken = "keep-me", TokenEndpoint = "https://auth.example.com/token",
        };

        var refreshed = await flow.RefreshAsync(state, CancellationToken.None);

        Assert.Equal("AT2", refreshed!.AccessToken);
        Assert.Equal("keep-me", refreshed.RefreshToken);
    }

    [Fact]
    public async Task RefreshAsync_NoRefreshToken_ReturnsNull()
    {
        var flow = new McpOAuthFlow(new FakeReceiver(), new FlowHandler());

        Assert.Null(await flow.RefreshAsync(new McpOAuthState { ClientId = "c" }, CancellationToken.None));
    }

    // ── RFC 9207: the issuer of the authorization response ────────────────────

    private const string Issuer = "https://auth.example.com";

    private static bool RedeemedACode(FlowHandler h) => h.Calls.Any(c => c.Url.EndsWith("/token") && c.Body.Contains("grant_type=authorization_code"));

    [Fact]
    public async Task AResponseFromTheRecordedIssuer_IsRedeemed_AndTheIssuerKept()
    {
        var handler = new FlowHandler { IssAdvertised = true };
        var flow = new McpOAuthFlow(new FakeReceiver { Iss = Issuer }, handler);

        var state = await flow.AuthorizeAsync(new McpOAuthServer(Server), existing: null, CancellationToken.None);

        Assert.Equal("AT", state.AccessToken);
        Assert.Equal(Issuer, state.Issuer);
    }

    [Theory]
    [InlineData(false)]   // a present iss is compared even when the metadata does not advertise it
    [InlineData(true)]
    public async Task AResponseFromAnotherIssuer_NeverReachesTheTokenEndpoint(bool advertised)
    {
        // ⚠ The mix-up attack: a code another authorization server delivered must not be sent to this one's token endpoint.
        var handler = new FlowHandler { IssAdvertised = advertised };
        var flow = new McpOAuthFlow(new FakeReceiver { Iss = "https://evil.example.com" }, handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => flow.AuthorizeAsync(new McpOAuthServer(Server), existing: null, CancellationToken.None));

        Assert.Contains("https://evil.example.com", ex.Message);
        Assert.Contains("RFC 9207", ex.Message);
        Assert.False(RedeemedACode(handler));
    }

    [Fact]
    public async Task AnIssuerComparedAsWritten_NotNormalised()
    {
        // RFC 9207 §2.4: no case folding, no trailing-slash rule — a different string is a different issuer.
        var handler = new FlowHandler();
        var flow = new McpOAuthFlow(new FakeReceiver { Iss = Issuer + "/" }, handler);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => flow.AuthorizeAsync(new McpOAuthServer(Server), existing: null, CancellationToken.None));
        Assert.False(RedeemedACode(handler));
    }

    [Fact]
    public async Task AnAdvertisedIssuerThatTheResponseLeavesOut_IsRefused()
    {
        var handler = new FlowHandler { IssAdvertised = true };
        var flow = new McpOAuthFlow(new FakeReceiver { Iss = null }, handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => flow.AuthorizeAsync(new McpOAuthServer(Server), existing: null, CancellationToken.None));

        Assert.Contains("RFC 9207", ex.Message);
        Assert.False(RedeemedACode(handler));
    }

    [Fact]
    public async Task NeitherAdvertisedNorSent_TheResponseIsRedeemed()
    {
        // Reference arm: the authorization servers of today, which do not send iss, keep signing in.
        var handler = new FlowHandler();
        var flow = new McpOAuthFlow(new FakeReceiver { Iss = null }, handler);

        Assert.Equal("AT", (await flow.AuthorizeAsync(new McpOAuthServer(Server), existing: null, CancellationToken.None)).AccessToken);
        Assert.True(RedeemedACode(handler));
    }

    [Fact]
    public async Task AnErrorFromAnotherIssuer_IsNeitherActedOnNorShown()
    {
        var flow = new McpOAuthFlow(new FakeReceiver { Iss = "https://evil.example.com", Error = "access_denied" }, new FlowHandler());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => flow.AuthorizeAsync(new McpOAuthServer(Server), existing: null, CancellationToken.None));

        Assert.Contains("RFC 9207", ex.Message);
        Assert.DoesNotContain("access_denied", ex.Message);
        Assert.DoesNotContain("the user said no", ex.Message);
    }

    [Fact]
    public async Task AnErrorFromTheRightIssuer_IsNamed_WithItsDescription()
    {
        var flow = new McpOAuthFlow(new FakeReceiver { Iss = Issuer, Error = "access_denied" }, new FlowHandler());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => flow.AuthorizeAsync(new McpOAuthServer(Server), existing: null, CancellationToken.None));

        Assert.Contains("Authorization denied: access_denied (the user said no)", ex.Message);
    }

    [Fact]
    public async Task ARedirectWithoutACode_IsNamed()
    {
        var flow = new McpOAuthFlow(new FakeReceiver { Code = "" }, new FlowHandler());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => flow.AuthorizeAsync(new McpOAuthServer(Server), existing: null, CancellationToken.None));

        Assert.Contains("no code", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Registration bound to its issuer ──────────────────────────────────────

    [Fact]
    public async Task ARegistration_SaysTheClientIsNative()
    {
        // A desktop client with a loopback redirect: left out, an OpenID provider assumes "web" and refuses the redirect.
        var handler = new FlowHandler();
        await new McpOAuthFlow(new FakeReceiver(), handler).AuthorizeAsync(new McpOAuthServer(Server), existing: null, CancellationToken.None);

        var register = handler.Calls.Single(c => c.Url.EndsWith("/register")).Body;
        Assert.Contains("\"application_type\":\"native\"", register);
    }

    [Fact]
    public async Task AClientRegisteredWithAnotherIssuer_IsRegisteredAgain()
    {
        var handler  = new FlowHandler();
        var existing = new McpOAuthState
        {
            ClientId = "client-of-the-old-as", Issuer = "https://old-as.example.com", Resource = "https://mcp.example.com/mcp",
        };

        var state = await new McpOAuthFlow(new FakeReceiver(), handler).AuthorizeAsync(new McpOAuthServer(Server), existing, CancellationToken.None);

        Assert.Equal("dcr-client-123", state.ClientId);
        Assert.Equal(Issuer, state.Issuer);
        Assert.Contains(handler.Calls, c => c.Url.EndsWith("/register"));
    }

    [Fact]
    public async Task AClientRegisteredWithTheSameIssuer_IsReused()
    {
        // Reference arm of the one above.
        var handler  = new FlowHandler();
        var existing = new McpOAuthState { ClientId = "same-as-client", Issuer = Issuer, Resource = "https://mcp.example.com/mcp" };

        var state = await new McpOAuthFlow(new FakeReceiver(), handler).AuthorizeAsync(new McpOAuthServer(Server), existing, CancellationToken.None);

        Assert.Equal("same-as-client", state.ClientId);
        Assert.DoesNotContain(handler.Calls, c => c.Url.EndsWith("/register"));
    }
}
