using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Inferpal.Services.Mcp.OAuth;

/// <summary>
/// Real <see cref="IAuthCodeReceiver"/>: opens the authorization URL in the user's default browser and
/// captures the redirect on a one-shot loopback <see cref="HttpListener"/> bound to an ephemeral port
/// (RFC 8252 native-app pattern). Not unit-tested — it drives a real browser and socket.
/// </summary>
internal sealed class LoopbackAuthCodeReceiver : IAuthCodeReceiver
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _timeout;

    /// <param name="timeout">How long to wait for the browser redirect. Tests pass a short one;
    /// nothing else does.</param>
    public LoopbackAuthCodeReceiver(TimeSpan? timeout = null)
    {
        _timeout    = timeout ?? DefaultTimeout;
        Port        = FreeLoopbackPort();
        RedirectUri = $"http://127.0.0.1:{Port}/callback";
    }

    public int Port { get; }
    public string RedirectUri { get; }

    public async Task<(string Code, string State)> GetAuthorizationCodeAsync(string authorizationUrl, CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            // ⚠ The FOURTH outcome, and it was outside the classification entirely: not "the wait
            // was interrupted" but "there was never a wait". FreeLoopbackPort probes a port and
            // releases it, so between the probe and this Start another process can take it — and
            // what reached the user was the operating system's own sentence, in the machine's
            // display language, naming neither the address nor what to do.
            throw new InvalidOperationException(
                $"Could not listen on {RedirectUri} for the authorization redirect: {ex.Message} "
                + "Another process may have taken the port; retry the connection.", ex);
        }
        try
        {
            OpenBrowser(authorizationUrl);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);
            var contextTask = listener.GetContextAsync();
            using (cts.Token.Register(listener.Stop))
            {
                var context = await AwaitCallbackAsync(contextTask, ct, cts.Token).ConfigureAwait(false);
                var query   = context.Request.QueryString;
                var error   = query["error"];
                var code    = query["code"];
                var state   = query["state"] ?? string.Empty;

                await WriteResponseAsync(context.Response, error).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(error))
                    throw new InvalidOperationException($"Authorization denied: {error}");
                if (string.IsNullOrEmpty(code))
                    throw new InvalidOperationException("Authorization redirect carried no code.");
                return (code!, state);
            }
        }
        finally
        {
            if (listener.IsListening) listener.Stop();
        }
    }

    /// <summary>
    /// The redirect, or a failure that <b>names its cause</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ Stopping the listener is how the wait is interrupted, and an interrupted
    /// <see cref="HttpListener.GetContextAsync"/> throws an <see cref="HttpListenerException"/>
    /// whose message is the operating system's own I/O sentence — <i>"the I/O operation has been
    /// aborted because of either a thread exit or an application request"</i>, in the machine's
    /// display language. It names nothing, and it is what the user was shown both when they
    /// cancelled and when the five minutes ran out: two situations with two different remedies.
    /// </para>
    /// <para>
    /// ⚠ And cancellation arrived as the <b>wrong type</b>. Everywhere else in this product
    /// cancellation is <see cref="OperationCanceledException"/> — the one exception callers let
    /// through and swallow on purpose. Coming out as a socket error, a cancelled sign-in was
    /// recorded and displayed as a failed one.
    /// </para>
    /// </remarks>
    private async Task<HttpListenerContext> AwaitCallbackAsync(
        Task<HttpListenerContext> contextTask, CancellationToken ct, CancellationToken deadline)
    {
        try
        {
            return await contextTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ⚠ The decision is read off the TOKENS, never off the exception type: which type a
            // stopped listener throws is the platform's choice — HttpListenerException here,
            // ObjectDisposedException elsewhere — and this product ships on three of them. The
            // tokens are the only thing that actually knows why the wait ended.
            //
            // The caller's cancellation comes first: it is the only one of the three that is not a
            // failure at all.
            if (Classify(ct, deadline) is { } named) throw named;

            // Anything else really is a socket failure: it keeps its own cause.
            throw;
        }
    }

    /// <summary>
    /// Which interruption ended the wait, read off the TOKENS — or <c>null</c> when neither of them
    /// fired, which is the one case that really is a socket failure and keeps its own cause.
    /// </summary>
    /// <remarks>
    /// ⚠ Extracted so it can be measured WITHOUT a listener. The test used to run a real one and
    /// assert that nothing but the deadline could end a short wait — a claim about the platform:
    /// macOS's managed <c>HttpListener</c> faults on its own, and shortening the window (round 65)
    /// did not close it, the CI reproduced it identically. With explicit tokens there is no race
    /// left to lose, on any platform.
    /// </remarks>
    internal Exception? Classify(CancellationToken ct, CancellationToken deadline)
    {
        // The caller's cancellation comes first: it is the only one of the three that is not a
        // failure at all.
        if (ct.IsCancellationRequested)
            return new OperationCanceledException(ct);

        if (deadline.IsCancellationRequested)
            return new TimeoutException(
                $"No authorization redirect arrived within {_timeout.TotalMinutes:0.#} minutes. "
                + "The sign-in page was never completed in the browser, or it redirected "
                + $"somewhere other than {RedirectUri}.");

        return null;
    }

    private static int FreeLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;   // Dispose closes the probe socket
    }

    /// <summary>
    /// Opens <paramref name="url"/> in the default browser — after checking it is one.
    /// </summary>
    /// <remarks>
    /// <c>UseShellExecute = true</c> is what makes "open in the user's browser" work, and it is also
    /// what makes this line dangerous: ShellExecute runs whatever the scheme is registered to, so a
    /// non-web URL here is a program launch. The string is built from the authorization server's
    /// advertised <c>authorization_endpoint</c>, i.e. it comes from the far end. The flow validates
    /// it at discovery (<c>McpOAuthMetadata.RequireWebEndpoint</c>); this second check is here
    /// because the dangerous call is <b>here</b>, and a future caller reaching this class by another
    /// path would not inherit the first one.
    /// </remarks>
    private static void OpenBrowser(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Diagnostics.Record("McpOAuth", $"Refused to open a non-web authorization URL: {url}");
            return;   // the listener then simply times out, which is the honest outcome
        }

        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* if the browser can't be launched the listener simply times out */ }
    }

    private static async Task WriteResponseAsync(HttpListenerResponse response, string? error)
    {
        var html = error is null
            ? "<html><body style='font-family:sans-serif'><h3>Inferpal — authorization complete</h3>You can close this tab.</body></html>"
            : $"<html><body style='font-family:sans-serif'><h3>Inferpal — authorization failed</h3>{WebUtility.HtmlEncode(error)}</body></html>";
        var bytes = System.Text.Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html";
        response.ContentLength64 = bytes.Length;
        try
        {
            await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            response.OutputStream.Close();
        }
        catch { /* client closed the tab */ }
    }
}
