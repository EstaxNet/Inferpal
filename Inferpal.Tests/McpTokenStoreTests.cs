using System.IO;
using Inferpal.Services.Mcp.OAuth;
using Xunit;

namespace Inferpal.Tests;

public class McpTokenStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"mcp-oauth-test-{Guid.NewGuid():N}.dat");

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { /* best effort */ }
    }

    // Identity "encryption" so the (de)serialization logic is exercised without DPAPI.
    private McpTokenStore PlainStore() => new(_path, protect: b => b, unprotect: b => b);

    [Fact]
    public void SaveThenGet_RoundTripsState()
    {
        var store = PlainStore();
        var state = new McpOAuthState
        {
            ClientId = "cid", ClientSecret = "secret",
            AccessToken = "at", RefreshToken = "rt",
            ExpiresAtUtc = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero),
            TokenEndpoint = "https://auth/token", Resource = "https://mcp",
            Scopes = ["a", "b"],
        };

        store.Save("srv", state);
        var got = store.Get("srv");

        Assert.NotNull(got);
        Assert.Equal("cid", got!.ClientId);
        Assert.Equal("rt", got.RefreshToken);
        Assert.Equal(state.ExpiresAtUtc, got.ExpiresAtUtc);
        Assert.Equal(["a", "b"], got.Scopes);
    }

    [Fact]
    public void Get_UnknownServer_ReturnsNull() => Assert.Null(PlainStore().Get("nope"));

    [Fact]
    public void Persists_AcrossInstances()
    {
        PlainStore().Save("srv", new McpOAuthState { AccessToken = "at" });

        // A fresh store over the same file must read what the first one wrote.
        var reloaded = new McpTokenStore(_path, protect: b => b, unprotect: b => b);
        Assert.Equal("at", reloaded.Get("srv")!.AccessToken);
    }

    [Fact]
    public void Remove_DeletesEntry()
    {
        var store = PlainStore();
        store.Save("a", new McpOAuthState { AccessToken = "1" });
        store.Save("b", new McpOAuthState { AccessToken = "2" });

        store.Remove("a");

        Assert.Null(store.Get("a"));
        Assert.Equal("2", store.Get("b")!.AccessToken);
    }

    [Fact]
    public void UndecryptableFile_IsTreatedAsEmpty()
    {
        File.WriteAllBytes(_path, [1, 2, 3, 4]);
        var store = new McpTokenStore(_path, protect: b => b, unprotect: _ => throw new InvalidOperationException("bad key"));

        Assert.Null(store.Get("srv"));   // no throw
    }

    [Theory]
    [InlineData(null, false)]                 // no token
    [InlineData("at", true)]                  // token, no expiry
    public void HasUsableAccessToken_NoExpiry(string? token, bool usable)
    {
        var s = new McpOAuthState { AccessToken = token };
        Assert.Equal(usable, s.HasUsableAccessToken(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void HasUsableAccessToken_HonorsExpirySkew()
    {
        var soon = new McpOAuthState { AccessToken = "at", ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(10) };
        var later = new McpOAuthState { AccessToken = "at", ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10) };

        Assert.False(soon.HasUsableAccessToken(TimeSpan.FromSeconds(30)));   // within skew ⇒ refresh
        Assert.True(later.HasUsableAccessToken(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void DpapiRoundTrip_Works()
    {
        // Exercises the real default DPAPI protect/unprotect (Windows, CurrentUser).
        var store = new McpTokenStore(_path);
        store.Save("srv", new McpOAuthState { AccessToken = "secret-token" });

        var reloaded = new McpTokenStore(_path);
        Assert.Equal("secret-token", reloaded.Get("srv")!.AccessToken);

        // The on-disk bytes must not contain the plaintext token.
        var raw = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(_path));
        Assert.DoesNotContain("secret-token", raw);
    }
}
