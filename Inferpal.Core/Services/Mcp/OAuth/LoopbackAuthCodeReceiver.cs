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
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public LoopbackAuthCodeReceiver()
    {
        Port = FreeLoopbackPort();
        RedirectUri = $"http://127.0.0.1:{Port}/callback";
    }

    public int Port { get; }
    public string RedirectUri { get; }

    public async Task<(string Code, string State)> GetAuthorizationCodeAsync(string authorizationUrl, CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        listener.Start();
        try
        {
            OpenBrowser(authorizationUrl);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            var contextTask = listener.GetContextAsync();
            using (cts.Token.Register(listener.Stop))
            {
                var context = await contextTask.ConfigureAwait(false);
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

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    private static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
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
