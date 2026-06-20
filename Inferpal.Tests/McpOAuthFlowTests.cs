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

        public Task<(string Code, string State)> GetAuthorizationCodeAsync(string authorizationUrl, CancellationToken ct)
        {
            LastAuthUrl = authorizationUrl;
            var m = Regex.Match(authorizationUrl, @"[?&]state=([^&]+)");
            var state = ReturnWrongState ? "WRONG" : (m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : "");
            return Task.FromResult((Code, state));
        }
    }

    private sealed class FlowHandler : HttpMessageHandler
    {
        public List<(string Method, string Url, string Body)> Calls { get; } = [];
        public bool IncludeRegistration { get; set; } = true;
        public string TokenJson { get; set; } = """{ "access_token":"AT", "refresh_token":"RT", "expires_in":3600 }""";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url  = request.RequestUri!.AbsoluteUri;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method.Method, url, body));

            if (request.Method == HttpMethod.Get && url.Contains("oauth-protected-resource"))
                return Json("""{ "resource":"https://mcp.example.com", "authorization_servers":["https://auth.example.com"] }""");
            if (request.Method == HttpMethod.Get && url.Contains("oauth-authorization-server"))
                return Json(IncludeRegistration ? AsmWithRegistration : AsmNoRegistration);
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
        var existing = new McpOAuthState { ClientId = "prev-client", ClientSecret = "sek" };

        var state = await flow.AuthorizeAsync(new McpOAuthServer(Server), existing, CancellationToken.None);

        Assert.Equal("prev-client", state.ClientId);
        Assert.DoesNotContain(handler.Calls, c => c.Url.EndsWith("/register"));
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
}
