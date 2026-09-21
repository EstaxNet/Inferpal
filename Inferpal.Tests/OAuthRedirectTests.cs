using System.Net;
using Inferpal.Services;
using Inferpal.Services.Mcp.OAuth;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The OAuth discovery client does not follow a redirect — the third guarantee the repository's
/// other HTTP clients state in the same words, on the one client that follows URLs a <b>remote
/// party</b> chose.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <c>McpOAuthMetadata</c> refuses an endpoint that is not https (or http on loopback) and says
/// why: <i>"this value comes from the remote server"</i>. A redirect walks straight around that
/// check — the guard only ever sees the address before the bounce — and it is exactly the sentence
/// <c>FetchUrlTool</c>, <c>DocCrawler</c> and <c>WebSearchTool</c> each carry: <i>"an automatic
/// redirect lets a public URL bounce the request onto 127.0.0.1 or 169.254.169.254 without passing
/// the guard again"</i>. Three clients said it; the fourth did not do it.
/// </para>
/// <para>
/// ⚠ Measured against the PRODUCTION client. Injecting a handler would have measured the test's own
/// stub — a custom <c>HttpMessageHandler</c> never auto-redirects — so the flow is driven against a
/// real loopback server. Nothing here races a clock: the assertion is on which requests ARRIVED.
/// </para>
/// </remarks>
public sealed class OAuthRedirectTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly List<string> _hits = [];
    private readonly string _prefix;

    public OAuthRedirectTests()
    {
        var port = FreePort();
        _prefix  = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    /// <remarks>
    /// ⚠ <b>Pure cleanup, and BOTH calls can throw.</b> <c>Stop()</c> was guarded and
    /// <c>Dispose()</c> — one line below — was not: on the managed <c>HttpListener</c> (every
    /// non-Windows leg) <c>Dispose</c> re-resolves the endpoint and raises
    /// <i>Address already in use</i> once the port has been taken again. The test body had already
    /// PASSED; xUnit then reports the class as failed, naming the cleanup instead of the subject.
    /// ⚠ And the cost is larger than one red test: <b>a gating leg that reddens for something that
    /// is not the product is how a gating leg gets turned off again</b> — and this one alone sees
    /// case-folding and ancestor symlinks.
    /// </remarks>
    public void Dispose()
    {
        try { _listener.Stop(); } catch { }
        try { ((IDisposable)_listener).Dispose(); } catch { }
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }

            var path = ctx.Request.Url!.AbsolutePath;
            lock (_hits) _hits.Add(path);

            if (path == "/bounce")
            {
                ctx.Response.StatusCode = 302;
                ctx.Response.Headers["Location"] = _prefix + "landed";
            }
            else if (path == "/direct")
            {
                var body = System.Text.Encoding.UTF8.GetBytes("""{"issuer":"x"}""");
                ctx.Response.StatusCode = 200;
                await ctx.Response.OutputStream.WriteAsync(body);
            }
            else
            {
                ctx.Response.StatusCode = 200;
            }
            ctx.Response.Close();
        }
    }

    private bool WasHit(string path)
    {
        lock (_hits) return _hits.Contains(path);
    }

    private static McpOAuthFlow NewFlow() => new(new NeverCalledReceiver());

    [Fact]
    public async Task ARedirectedDiscovery_IsNotFollowed()
    {
        var body = await NewFlow().TryGetAsync(_prefix + "bounce", CancellationToken.None);

        // WITNESS: the bounce really was served, so "landed was not hit" measures the client and
        // not a server that never answered.
        Assert.True(WasHit("/bounce"));
        Assert.False(WasHit("/landed"));
        Assert.Null(body);
    }

    /// <summary>Reference arm: an ordinary document still comes back — the guarantee is about
    /// redirects, not about refusing to fetch.</summary>
    [Fact]
    public async Task ADirectDocument_IsStillFetched()
    {
        var body = await NewFlow().TryGetAsync(_prefix + "direct", CancellationToken.None);

        Assert.Equal("""{"issuer":"x"}""", body);
    }

    /// <summary>
    /// And the step says why it came back empty: a refused discovery used to be indistinguishable
    /// from a server that simply has no metadata, so the sign-in failed further down with no trace
    /// of the step that actually went wrong.
    /// </summary>
    [Fact]
    public async Task ADiscoveryThatFailed_LeavesATrace()
    {
        Diagnostics.Clear();

        await NewFlow().TryGetAsync(_prefix + "bounce", CancellationToken.None);

        Assert.Contains(Diagnostics.Snapshot(),
                        e => e.Context == "McpOAuthFlow.Discovery" && e.Detail.Contains("302"));
    }

    /// <summary>A receiver that must never be reached: these tests drive discovery only.</summary>
    private sealed class NeverCalledReceiver : IAuthCodeReceiver
    {
        public int Port => 0;
        public string RedirectUri => "http://127.0.0.1:0/callback";
        public Task<(string Code, string State)> GetAuthorizationCodeAsync(string authorizationUrl, CancellationToken ct) =>
            throw new InvalidOperationException("the browser step is not part of these tests");
    }
}
