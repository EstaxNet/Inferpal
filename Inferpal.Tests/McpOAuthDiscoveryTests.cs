using System.Net;
using System.Net.Http;
using System.Text;
using Inferpal.Services.Mcp.OAuth;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Discovery as the MCP specification gives it. An issuer with a path (Entra, Keycloak, Okta) publishes its metadata
/// under that path, most of them as an OpenID Connect document only: looked for at the origin, nothing was found, the
/// guessed "/authorize" opened an error page and the sign-in waited five minutes to report a browser step "never
/// completed". And the scopes asked for were every scope the authorization server lists.
/// </summary>
public class McpOAuthDiscoveryTests
{
    private const string Issuer = "https://auth.example.com/realms/r";

    /// <summary>A Keycloak-like server: OpenID Connect discovery appended to the issuer's path, nothing else.</summary>
    private sealed class KeycloakLike(string? resourceScopes) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (Asked) Asked.Add(url);
            if (request.Method == HttpMethod.Get && url == "https://mcp.example.com/.well-known/oauth-protected-resource/mcp")
                return Json($$"""{ "resource":"https://mcp.example.com/mcp", "authorization_servers":["{{Issuer}}"]{{resourceScopes}} }""");
            if (request.Method == HttpMethod.Get && url == $"{Issuer}/.well-known/openid-configuration")
                return Json($$"""
                {
                  "issuer": "{{Issuer}}",
                  "authorization_endpoint": "{{Issuer}}/protocol/openid-connect/auth",
                  "token_endpoint": "{{Issuer}}/protocol/openid-connect/token",
                  "scopes_supported": ["openid", "profile", "email", "admin"]
                }
                """);
            if (request.Method == HttpMethod.Post && url.EndsWith("/token", StringComparison.Ordinal))
                return Json("""{ "access_token":"AT", "refresh_token":"RT", "expires_in":3600 }""");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string json) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    private sealed class Receiver : IAuthCodeReceiver
    {
        public string RedirectUri => "http://127.0.0.1:9999/callback";
        public string? LastAuthUrl { get; private set; }

        public Task<AuthorizationResponse> GetAuthorizationCodeAsync(string authorizationUrl, CancellationToken ct)
        {
            LastAuthUrl = authorizationUrl;
            var m = System.Text.RegularExpressions.Regex.Match(authorizationUrl, "[?&]state=([^&]+)");
            return Task.FromResult(new AuthorizationResponse("code", Uri.UnescapeDataString(m.Groups[1].Value), null, null, null));
        }
    }

    private static readonly McpOAuthServer Server = new(new Uri("https://mcp.example.com/mcp"), ClientId: "cid");

    [Fact]
    public async Task AnIssuerWithAPath_IsFoundThroughOpenIdConnectDiscovery()
    {
        var receiver = new Receiver();

        await new McpOAuthFlow(receiver, new KeycloakLike(null)).AuthorizeAsync(Server, existing: null, CancellationToken.None);

        Assert.StartsWith($"{Issuer}/protocol/openid-connect/auth?", receiver.LastAuthUrl);
    }

    [Fact]
    public async Task TheScopesAskedFor_AreTheResources_NotEveryOneTheServerLists()
    {
        var receiver = new Receiver();

        await new McpOAuthFlow(receiver, new KeycloakLike(""", "scopes_supported":["mcp.read"]"""))
            .AuthorizeAsync(Server, existing: null, CancellationToken.None);

        Assert.Contains("scope=mcp.read", receiver.LastAuthUrl);
        Assert.DoesNotContain("admin", receiver.LastAuthUrl);
    }

    [Fact]
    public async Task NoScopeNamedByTheResource_AsksForNone()
    {
        var receiver = new Receiver();

        await new McpOAuthFlow(receiver, new KeycloakLike(null)).AuthorizeAsync(Server, existing: null, CancellationToken.None);

        Assert.DoesNotContain("scope=", receiver.LastAuthUrl);
    }
}
