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
        public Task<(string Code, string State)> GetAuthorizationCodeAsync(string url, CancellationToken ct)
            => throw new InvalidOperationException("should not be called");
    }

    private McpStoredTokenProvider Provider(McpTokenStore store, string tokenJson) =>
        new("srv", store, new McpOAuthFlow(new ThrowingReceiver(), new TokenHandler(tokenJson)));

    [Fact]
    public async Task ReturnsStoredToken_WhenStillValid()
    {
        var store = Store();
        store.Save("srv", new McpOAuthState { AccessToken = "valid", ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1) });

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
            ClientId = "c", AccessToken = "old", RefreshToken = "rt",
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
        store.Save("srv", new McpOAuthState { AccessToken = "old", ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-10) });

        Assert.Null(await Provider(store, "{}").GetAccessTokenAsync(CancellationToken.None));
    }
}
