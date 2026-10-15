using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Inferpal.Services.Mcp;
using Inferpal.Services.Mcp.OAuth;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A sign-in that could not be renewed NOW is not a sign-in to do again: only a refusal of the refresh token asks for one.
/// </summary>
/// <remarks>
/// Every failure of the renewal — the authorization server down (503), busy (429), unreachable, slow — came back as
/// "refused": the server's tools were withdrawn and its card asked for a full sign-in in the browser, for a sign-in that
/// still stood. And when Visual Studio and VS Code, which share the store, renewed with the same refresh token, the
/// authorization server that rotates it refused the second editor — which asked for a sign-in while the store already
/// held the first editor's fresh one.
/// </remarks>
public sealed class McpTokenRenewalTests : IDisposable
{
    private const string Res = "https://srv.example.com/mcp";
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"mcp-renewal-{Guid.NewGuid():N}.dat");

    public void Dispose() { try { File.Delete(_path); } catch { /* best-effort cleanup */ } }

    private McpTokenStore Store() => new(_path, protect: b => b, unprotect: b => b);

    private static McpOAuthState Expired(string refreshToken) => new()
    {
        Resource = Res, ClientId = "c", AccessToken = "old", RefreshToken = refreshToken,
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-10), TokenEndpoint = "https://auth.example.com/token",
    };

    /// <summary>Answers the token endpoint with <paramref name="answer"/>, after running <paramref name="meanwhile"/>.</summary>
    private sealed class TokenEndpoint(Func<HttpResponseMessage> answer, Action? meanwhile = null) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            meanwhile?.Invoke();
            return Task.FromResult(answer());
        }
    }

    private sealed class NoBrowser : IAuthCodeReceiver
    {
        public string RedirectUri => "http://127.0.0.1/callback";
        public Task<AuthorizationResponse> GetAuthorizationCodeAsync(string url, CancellationToken ct) =>
            throw new InvalidOperationException("no browser in a renewal");
    }

    private static HttpResponseMessage Status(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private McpStoredTokenProvider Provider(McpTokenStore store, HttpMessageHandler endpoint) =>
        new("srv", Res, store, new McpOAuthFlow(new NoBrowser(), endpoint), rotationGrace: TimeSpan.Zero);

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task AnAuthorizationServerThatCannotAnswerNow_LeavesTheSignInStanding(HttpStatusCode code)
    {
        var store = Store();
        store.Save("srv", Expired("rt"));
        var provider = Provider(store, new TokenEndpoint(() => Status(code, "try later")));

        var failure = await Assert.ThrowsAsync<McpTokenRenewalException>(() => provider.GetAccessTokenAsync(CancellationToken.None));

        Assert.Contains(((int)code).ToString(System.Globalization.CultureInfo.InvariantCulture), failure.Message);
        Assert.Equal("rt", store.Get("srv")!.RefreshToken);   // kept: the next call renews it
    }

    [Fact]
    public async Task AnUnreachableAuthorizationServer_LeavesTheSignInStanding()
    {
        var store = Store();
        store.Save("srv", Expired("rt"));
        var provider = Provider(store, new TokenEndpoint(() => throw new HttpRequestException("No such host is known.")));

        var failure = await Assert.ThrowsAsync<McpTokenRenewalException>(() => provider.GetAccessTokenAsync(CancellationToken.None));
        Assert.Contains("No such host is known.", failure.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task ARefusedRefreshToken_AsksForASignIn(HttpStatusCode code)
    {
        // Reference arm: the one answer that means "sign in again".
        var store = Store();
        store.Save("srv", Expired("rt"));
        var provider = Provider(store, new TokenEndpoint(() => Status(code, """{ "error": "invalid_grant" }""")));

        Assert.Null(await provider.GetAccessTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ARenewalTheOtherEditorWon_IsUsed_InsteadOfAskingForASignIn()
    {
        var store = Store();
        store.Save("srv", Expired("rt-1"));
        // The other editor renews with the same refresh token first; the server rotates it and refuses ours.
        var endpoint = new TokenEndpoint(
            () => Status(HttpStatusCode.BadRequest, """{ "error": "invalid_grant" }"""),
            meanwhile: () =>
            {
                var other = Store();
                if (other.Get("srv")!.RefreshToken == "rt-1")
                    other.Save("srv", new McpOAuthState
                    {
                        Resource = Res, ClientId = "c", AccessToken = "renewed-by-the-other-editor", RefreshToken = "rt-2",
                        ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1), TokenEndpoint = "https://auth.example.com/token",
                    });
            });

        Assert.Equal("renewed-by-the-other-editor", await Provider(store, endpoint).GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal(1, endpoint.Calls);
    }

    [Fact]
    public async Task ARenewalStoppedByItsCaller_IsACancellation()
    {
        var store = Store();
        store.Save("srv", Expired("rt"));
        using var cts = new CancellationTokenSource();
        var provider = Provider(store, new TokenEndpoint(() => { cts.Cancel(); throw new TaskCanceledException(); }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetAccessTokenAsync(cts.Token));
    }

    [Fact]
    public async Task AServerWhoseSignInCannotBeRenewedNow_DoesNotAskForASignIn_AndSaysWhy()
    {
        var store = Store();
        store.Save("srv", Expired("rt"));
        var provider = Provider(store, new TokenEndpoint(() => Status(HttpStatusCode.ServiceUnavailable, "maintenance")));
        // The MCP server itself is never reached: the renewal fails before the request leaves.
        var server = new TokenEndpoint(() => throw new InvalidOperationException("the request left without a token"));
        await using var client = new McpHttpClient(
            new McpServerConfig("srv", null, [], new Dictionary<string, string>(), Url: Res), server, provider);

        Assert.False(await client.StartAsync(CancellationToken.None));

        Assert.False(client.NeedsAuthorization);
        Assert.Contains("could not be renewed", client.LastError);
        Assert.Contains("503", client.LastError);
        Assert.Equal(0, server.Calls);
    }
}
