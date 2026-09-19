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

    /// <summary>
    /// The three outcomes of an interrupted wait, decided on the TOKENS — measured without a
    /// listener at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ These used to run a real <c>HttpListener</c> and assert that nothing but the cancel (or
    /// the deadline) could end a short wait. That is a claim about the PLATFORM: macOS's managed
    /// listener faults on its own, and the CI went red on it TWICE — shortening the window did not
    /// close the instance, it only made the window smaller. With explicit tokens there is no race
    /// left to lose, on any platform, and the assertion is on the rule the product holds.
    /// </para>
    /// <para>
    /// ⚠ The end-to-end path keeps its cover: the reference arm above drives a real redirect
    /// through a real listener, and it COMPLETES rather than racing a clock.
    /// </para>
    /// </remarks>
    [Fact]
    public void ACancelledSignIn_IsCancellation_NotAFailure()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var ex = new LoopbackAuthCodeReceiver().Classify(cancelled.Token, CancellationToken.None);

        // The TYPE is the subject: it is what callers read so as not to report a failure.
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    /// <summary>The caller's cancellation wins over the deadline: it is the only one of the three
    /// that is not a failure, so it must never come out as one.</summary>
    [Fact]
    public void CancellationWins_WhenBothFired()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var ex = new LoopbackAuthCodeReceiver().Classify(cancelled.Token, cancelled.Token);

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    /// <summary>When the DEADLINE fired, the sentence names the address that was being waited on —
    /// "it did not work" sends the reader nowhere.</summary>
    [Fact]
    public void ADeadlineThatPasses_NamesWhatDidNotHappen()
    {
        using var expired = new CancellationTokenSource();
        expired.Cancel();

        var receiver = new LoopbackAuthCodeReceiver();
        var timeout  = Assert.IsType<TimeoutException>(
            receiver.Classify(CancellationToken.None, expired.Token));

        Assert.Contains(receiver.RedirectUri, timeout.Message, StringComparison.Ordinal);
        Assert.Contains("browser", timeout.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// REFERENCE ARM, and what keeps the third outcome alive: neither token fired, so the wait
    /// ended for a reason of its own — a genuine socket failure, which keeps its cause instead of
    /// being dressed up as a timeout.
    /// </summary>
    [Fact]
    public void AFailureThatIsNeither_KeepsItsOwnCause() =>
        Assert.Null(new LoopbackAuthCodeReceiver().Classify(CancellationToken.None, CancellationToken.None));

    /// <summary>
    /// The FOURTH outcome, which sat outside the classification entirely: not "the wait was
    /// interrupted" but "there was never a wait".
    /// </summary>
    /// <remarks>
    /// ⚠ <c>FreeLoopbackPort</c> probes a port and releases it, so another process can take it
    /// before <c>Start</c> binds. What reached the user was the operating system's own sentence, in
    /// the machine's display language, naming neither the address nor what to do — the very shape
    /// this class exists to remove, one step earlier in the method.
    /// </remarks>
    [Fact]
    public async Task AListenerThatCannotBeOpened_NamesTheAddress()
    {
        var receiver = new LoopbackAuthCodeReceiver();

        // WITNESS: the port really is taken, by us, for the whole call.
        using var squatter = new System.Net.Sockets.TcpListener(IPAddress.Loopback, receiver.Port);
        squatter.Start();

        var ex = await Record.ExceptionAsync(
            () => receiver.GetAuthorizationCodeAsync(NoBrowser, CancellationToken.None));

        var named = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains(receiver.RedirectUri, named.Message, StringComparison.Ordinal);
        Assert.IsType<HttpListenerException>(named.InnerException);
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
