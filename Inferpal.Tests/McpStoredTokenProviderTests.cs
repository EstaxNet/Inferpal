using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Inferpal.Services.Mcp.OAuth;
using Xunit;

namespace Inferpal.Tests;

public class McpStoredTokenProviderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"mcp-tok-{Guid.NewGuid():N}.dat");

    public void Dispose() { try { if (File.Exists(_path)) File.Delete(_path); } catch { } }

    private McpTokenStore Store() => new(_path, protect: b => b, unprotect: b => b);

    private sealed class TokenHandler(string json) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingReceiver : IAuthCodeReceiver
    {
        public string RedirectUri => "http://127.0.0.1/callback";
        public Task<AuthorizationResponse> GetAuthorizationCodeAsync(string url, CancellationToken ct)
            => throw new InvalidOperationException("should not be called");
    }

    private const string Res = "https://srv.example.com/mcp";

    private McpStoredTokenProvider Provider(McpTokenStore store, string tokenJson, string resource = Res) =>
        new("srv", resource, store, new McpOAuthFlow(new ThrowingReceiver(), new TokenHandler(tokenJson)));

    /// <summary>
    /// ⚠ The store is keyed by the server's NAME. Pointed at another URL — or a deleted server's name reused — the token
    /// obtained for the first server went to the second as its bearer, and an expired one was refreshed at the first
    /// server's authorization server and sent there too. A state obtained for another resource is never used.
    /// </summary>
    [Fact]
    public async Task AStateObtainedForAnotherServer_IsNeverSent_NorRefreshed()
    {
        var store = Store();
        store.Save("srv", new McpOAuthState
        {
            Resource = "https://a.example.com/mcp", AccessToken = "token-of-a", RefreshToken = "rt-of-a",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1), TokenEndpoint = "https://auth.a.example.com/token",
        });
        var handler = new TokenHandler("""{ "access_token":"fresh-of-a", "expires_in":3600 }""");
        var provider = new McpStoredTokenProvider("srv", "https://b.other.com/mcp", store,
                                                  new McpOAuthFlow(new ThrowingReceiver(), handler));

        Assert.Null(await provider.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal(0, handler.Calls);

        // Reference arm: the same state, asked for by its own server, is sent.
        Assert.Equal("token-of-a", await Provider(store, "{}", "https://a.example.com/mcp").GetAccessTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReturnsStoredToken_WhenStillValid()
    {
        var store = Store();
        store.Save("srv", new McpOAuthState { Resource = Res, AccessToken = "valid", ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1) });

        Assert.Equal("valid", await Provider(store, "{}").GetAccessTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReturnsNull_WhenNoStateOrNoToken()
    {
        Assert.Null(await Provider(Store(), "{}").GetAccessTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RefreshesExpiredToken_AndPersistsResult()
    {
        var store = Store();
        store.Save("srv", new McpOAuthState
        {
            Resource = Res, ClientId = "c", AccessToken = "old", RefreshToken = "rt",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-10),   // expired
            TokenEndpoint = "https://auth/token",
        });

        var token = await Provider(store, """{ "access_token":"fresh", "expires_in":3600 }""")
            .GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal("fresh", token);
        Assert.Equal("fresh", store.Get("srv")!.AccessToken);   // persisted
    }

    [Fact]
    public async Task ExpiredWithoutRefreshToken_ReturnsNull()
    {
        var store = Store();
        store.Save("srv", new McpOAuthState { Resource = Res, AccessToken = "old", ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-10) });

        Assert.Null(await Provider(store, "{}").GetAccessTokenAsync(CancellationToken.None));
    }
}
