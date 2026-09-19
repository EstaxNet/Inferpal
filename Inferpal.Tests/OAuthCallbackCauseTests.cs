using System.Net;
using System.Net.Http;
using Inferpal.Services.Mcp.OAuth;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An interrupted authorization wait did not say <b>which</b> interruption it was, and the one
/// outcome that is not a failure came out under the type of the failures.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>Raw measurement, before the fix</b>: cancelling the connection to an MCP server surfaced
/// <c>System.Net.HttpListenerException: The I/O operation has been aborted because of either a
/// thread exit or an application request.</c> — the operating system's own sentence, in the
/// machine's display language. It names neither the server, nor the redirect URI, nor what to do
/// about it, and it was <b>the same</b> after five minutes of waiting: two situations, two
/// remedies, one message.
/// </para>
/// <para>
/// ⚠ <b>And the type was wrong.</b> Everywhere else in this product cancellation is an
/// <c>OperationCanceledException</c> — the one exception callers let through and swallow on
/// purpose. Coming out as a socket error, a sign-in <b>cancelled by the user</b> was recorded and
/// displayed as a <b>failed</b> one.
/// </para>
/// <para>
/// ⚠ <b>What the third branch preserves</b>: a genuine socket failure keeps its own cause.
/// Replacing all three outcomes with a single message would have rebuilt the defect backwards.
/// </para>
/// </remarks>
public class OAuthCallbackCauseTests
{
    /// <summary>
    /// A non-web authorization URL: <c>OpenBrowser</c> refuses it before any launch, so nothing
    /// opens and the listener waits on loopback — the only setup that makes this class executable
    /// from the suite.
    /// </summary>
    private const string NoBrowser = "file:///c:/inferpal-tests/never-launched";

    private static async Task<string> GetAsync(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        return await http.GetStringAsync(url);
    }

    [Fact]
    public async Task ACompletedRedirect_YieldsTheCodeAndState()
    {
        // REFERENCE ARM: without it, the three failures below could all come from a setup that does
        // not work at all.
        var receiver = new LoopbackAuthCodeReceiver();
        var waiting  = receiver.GetAuthorizationCodeAsync(NoBrowser, CancellationToken.None);

        var page = await GetAsync($"{receiver.RedirectUri}?code=abc123&state=xyz");
        var (code, state) = await waiting;

        Assert.Equal("abc123", code);
        Assert.Equal("xyz", state);
        Assert.Contains("authorization complete", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelledSignIn_IsCancellation_NotAFailure()
    {
        var receiver = new LoopbackAuthCodeReceiver();
        using var cts = new CancellationTokenSource();

        // ⚠ Cancelled at once, not after a wait: what is being measured is the CLASSIFICATION of
        // an interrupted wait, and any window left open is a window in which the listener can fault
        // on its own — see the remark on the deadline test below.
        var waiting = receiver.GetAuthorizationCodeAsync(NoBrowser, cts.Token);
        cts.Cancel();

        var ex = await Record.ExceptionAsync(() => waiting);

        // The TYPE is the subject: it is what callers read so as not to report a failure.
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    /// <summary>
    /// When the DEADLINE has fired, the answer is a <see cref="TimeoutException"/> naming the
    /// address — whatever exception the platform chose to end the wait with.
    /// </summary>
    /// <remarks>
    /// ⚠ The deadline is 1 ms, and that is the point rather than an optimisation. At 400 ms this
    /// test went red on macOS and green on a rerun: the managed <c>HttpListener</c> there can fault
    /// on its own inside the window, and the product then classified it — correctly — as a genuine
    /// socket failure. The test was asserting that nothing but the deadline can end a 400 ms wait,
    /// which is a claim about the platform, not about the product. Firing the deadline first makes
    /// the assertion deterministic everywhere AND keeps it on the rule: whatever arrives, the
    /// deadline token is the one that had fired, so the deadline's sentence is the right one.
    /// ⚠ An intermittent red on the one CI leg that sees certain classes alone is worse than no
    /// test: it is how that leg stops being read.
    /// </remarks>
    [Fact]
    public async Task ADeadlineThatPasses_NamesWhatDidNotHappen()
    {
        var receiver = new LoopbackAuthCodeReceiver(TimeSpan.FromMilliseconds(1));

        var ex = await Record.ExceptionAsync(
            () => receiver.GetAuthorizationCodeAsync(NoBrowser, CancellationToken.None));

        var timeout = Assert.IsType<TimeoutException>(ex);
        // The sentence names the address that was being waited on — without it, "it did not work"
        // sends the reader nowhere. And it is distinct from cancellation, which is not a failure.
        Assert.Contains(receiver.RedirectUri, timeout.Message, StringComparison.Ordinal);
        Assert.Contains("browser", timeout.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARefusalFromTheServer_KeepsNamingItself()
    {
        // WITNESS: the named outcomes that already existed were not swallowed by the new handling
        // of interruptions.
        var receiver = new LoopbackAuthCodeReceiver();
        var waiting  = receiver.GetAuthorizationCodeAsync(NoBrowser, CancellationToken.None);

        var page = await GetAsync($"{receiver.RedirectUri}?error=access_denied");
        var ex   = await Record.ExceptionAsync(() => waiting);

        Assert.Contains("access_denied", Assert.IsType<InvalidOperationException>(ex).Message,
                        StringComparison.Ordinal);
        Assert.Contains("access_denied", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARedirectWithoutACode_KeepsNamingItself()
    {
        var receiver = new LoopbackAuthCodeReceiver();
        var waiting  = receiver.GetAuthorizationCodeAsync(NoBrowser, CancellationToken.None);

        await GetAsync($"{receiver.RedirectUri}?state=xyz");
        var ex = await Record.ExceptionAsync(() => waiting);

        Assert.Contains("no code", Assert.IsType<InvalidOperationException>(ex).Message,
                        StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheLoopbackUriIsWhatRFC8252Asks()
    {
        // WITNESS for the setup: the address really is loopback on an ephemeral port, without which
        // the tests above would be talking to something other than what they think.
        var uri = new Uri(new LoopbackAuthCodeReceiver().RedirectUri);

        Assert.Equal("127.0.0.1", uri.Host);
        Assert.Equal("/callback", uri.AbsolutePath);
        Assert.True(uri.Port > 1024, $"Port inattendu : {uri.Port}");
        Assert.Equal(Uri.UriSchemeHttp, uri.Scheme);
    }
}
