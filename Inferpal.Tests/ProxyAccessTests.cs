using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Localization;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The proxy access block: a method and its fields are read from <c>requestHeaders</c> and written back to it, and the
/// Test says hop by hop where a request stopped (<see cref="ProxyAccess"/>, <see cref="ProxyCheck"/>).
/// </summary>
public class ProxyAccessTests
{
    [Fact]
    public void EachMethod_WritesItsHeaders_AndIsReadBackFromThem()
    {
        var basic = ProxyAccess.Write(new(ProxyMethod.Basic, User: "dams", Password: "pässword:1"));
        Assert.Equal("Authorization: Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("dams:pässword:1")), basic);
        Assert.Equal((ProxyMethod.Basic, "dams", "pässword:1"), (ProxyAccess.Read(basic).Method, ProxyAccess.Read(basic).User, ProxyAccess.Read(basic).Password));

        var bearer = ProxyAccess.Write(new(ProxyMethod.Bearer, Token: "  t0k3n "));
        Assert.Equal("Authorization: Bearer t0k3n", bearer);
        Assert.Equal("t0k3n", ProxyAccess.Read(bearer).Token);

        var cf = ProxyAccess.Write(new(ProxyMethod.Cloudflare, ClientId: "id.access", ClientSecret: "s3cr3t"));
        Assert.Equal("CF-Access-Client-Id: id.access\nCF-Access-Client-Secret: s3cr3t", cf);
        var read = ProxyAccess.Read(cf);
        Assert.Equal((ProxyMethod.Cloudflare, "id.access", "s3cr3t"), (read.Method, read.ClientId, read.ClientSecret));

        Assert.Equal(ProxyMethod.None, ProxyAccess.Read("  ").Method);
        Assert.Equal("", ProxyAccess.Write(new(ProxyMethod.None, User: "ignored")));
        Assert.Equal("", ProxyAccess.Write(new(ProxyMethod.Basic)));   // nothing typed: nothing sent
    }

    [Theory]
    [InlineData("Authorization: Basic not-base64!")]                                  // unreadable Basic
    [InlineData("Authorization: Bearer x\nX-Other: y")]                               // a second header
    [InlineData("CF-Access-Client-Id: a\nCF-Access-Client-Secret: b\nX-More: c")]    // Cloudflare plus one
    [InlineData("# a comment\nAuthorization: Bearer x")]                              // a line that is not a header
    [InlineData("X-Api-Key: k")]
    public void AnythingMoreThanAMethodWrites_IsCustom_AndKeptWordForWord(string text)
    {
        var form = ProxyAccess.Read(text);
        Assert.Equal(ProxyMethod.Custom, form.Method);
        Assert.Equal(text, ProxyAccess.Write(form));
    }

    private static async Task<ProxyReport> TestAsync(int port, string headers, string? apiKey = null)
    {
        var url = $"http://127.0.0.1:{port}";
        try { return await ProxyCheck.RunAsync(url, apiKey, headers, CancellationToken.None); }
        finally { ServerHeaders.Note(url, null); }
    }

    private const string Basic = "Authorization: Basic ZGFtczpwYXNz";   // dams:pass

    [Fact]
    public async Task AProxyThatRefusesTheCredentials_IsTheHopThatFailed_WithTheMethodsChecks()
    {
        using var proxy = new ServerHeadersTests.Server((_, _) => (401, null, "{}"));

        var report = await TestAsync(proxy.Port, Basic);

        Assert.Equal([HopState.Passed, HopState.Failed, HopState.NotReached], report.Hops.Select(h => h.State));
        Assert.Equal(Strings.ProxyHopRefused("401 X"), report.Hops[1].Title);
        Assert.Equal(Strings.ProxySentBasic("dams"), report.Hops[1].Detail);          // the username, never the password
        Assert.Equal(ProxyCheck.ChecksFor(ProxyMethod.Basic), report.Hops[1].Checks);
        Assert.False(report.Passed);
    }

    [Fact]
    public async Task AProxyThatAsks_WhenNothingIsSent_SaysToPickAMethod()
    {
        using var proxy = new ServerHeadersTests.Server((_, _) => (401, null, "{}"));

        var report = await TestAsync(proxy.Port, "");

        Assert.Equal(Strings.ProxyHopAsks("401 X"), report.Hops[1].Title);
        Assert.Equal([Strings.ProxyCheckPickMethod], report.Hops[1].Checks);
    }

    [Fact]
    public async Task CloudflaresSignInRedirect_IsNamedAsThat_AndNotFollowed()
    {
        using var proxy = new ServerHeadersTests.Server((_, _) => (302, "https://team.cloudflareaccess.com/cdn-cgi/access/login", "{}"));

        var report = await TestAsync(proxy.Port, "CF-Access-Client-Id: a.access\nCF-Access-Client-Secret: b");

        Assert.Equal(Strings.ProxyHopSignInPage, report.Hops[1].Title);
        Assert.Equal(Strings.ProxyHopSignInSent, report.Hops[1].Detail);
        Assert.Single(proxy.Seen);   // one request: the redirect was not followed
    }

    [Fact]
    public async Task AModelServerBehindTheProxy_PassesEveryHop()
    {
        using var proxy = new ServerHeadersTests.Server((path, h) =>
            !h.ContainsKey("Authorization") ? (401, null, "{}")
            : path == "/api/tags" ? (200, null, """{"models":[]}""") : (200, null, "Ollama is running"));

        var report = await TestAsync(proxy.Port, Basic);

        Assert.All(report.Hops, h => Assert.Equal(HopState.Passed, h.State));
        Assert.Equal(InferenceProviderFactory.Ollama, report.Provider);
        Assert.Equal(Strings.ProxyHopLetThrough, report.Hops[1].Title);
    }

    [Fact]
    public void HalfACloudflareToken_WritesNothing_AndNonUtf8Credentials_StayOtherHeaders()
    {
        Assert.Equal("", ProxyAccess.Write(new(ProxyMethod.Cloudflare, ClientSecret: "only-the-secret")));
        // "dams:päss" in Latin-1: not UTF-8, so never opened as a username and a password re-encoded wrong.
        var latin1 = "Authorization: Basic " + Convert.ToBase64String([0x64, 0x61, 0x6D, 0x73, 0x3A, 0x70, 0xE4, 0x73, 0x73]);
        Assert.Equal(ProxyMethod.Custom, ProxyAccess.Read(latin1).Method);
    }

    [Fact]
    public async Task AWrongApiKey_WithoutAProxy_IsTheKeysHop_NeverTheProxys()
    {
        // An OpenAI-compatible server that wants its key on every path, and gets the wrong one.
        using var server = new ServerHeadersTests.Server((_, _) => (401, null, "{}"));

        var report = await TestAsync(server.Port, "", apiKey: "sk-wrong");

        Assert.Equal([HopState.Passed, HopState.Passed, HopState.Failed], report.Hops.Select(h => h.State));
        Assert.StartsWith(Strings.ProxyHopKeyRefused("")[..20], report.Hops[2].Title, StringComparison.Ordinal);
        Assert.Equal([Strings.ProxyCheckApiKey], report.Hops[2].Checks);
    }

    [Fact]
    public async Task ARedirectFollowedToAnotherHost_IsSaid_NeverLetThrough()
    {
        using var signIn = new ServerHeadersTests.Server((_, _) => (200, null, "<html>sign in</html>"));
        // localhost is another host than 127.0.0.1: a forward-auth sign-in page on its own host.
        using var proxy = new ServerHeadersTests.Server((_, _) => (302, $"http://localhost:{signIn.Port}/login", "{}"));

        var report = await TestAsync(proxy.Port, "");

        Assert.Equal(Strings.ProxyHopRedirected($"http://localhost:{signIn.Port}"), report.Hops[1].Title);
        Assert.Equal(HopState.NotReached, report.Hops[2].State);
    }

    [Fact]
    public async Task NothingListening_IsTheFirstHop()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();   // a port nothing listens on

        var report = await TestAsync(port, "");

        Assert.Equal([HopState.Failed, HopState.NotReached, HopState.NotReached], report.Hops.Select(h => h.State));
        Assert.Equal(Strings.ProxyHopUnreachable("127.0.0.1"), report.Hops[0].Title);
    }
}
