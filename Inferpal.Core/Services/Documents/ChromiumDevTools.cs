using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace Inferpal.Services.Documents;

/// <summary>
/// A capture by a Chromium browser (Edge, Chrome) driven through its DevTools protocol, the page's viewport set to the exact
/// size asked, as the DevTools' device toolbar sets it.
/// </summary>
/// <remarks>
/// ⚠ A headless Chromium keeps its window at least about 520 px wide whatever <c>--window-size</c> asks: from the command
/// line, a phone's 390 px is laid out at 496 and the picture cut at 390 — elements shown cut that a phone shows whole, a
/// phone's own breakage never shown. The protocol's <c>Emulation.setDeviceMetricsOverride</c> lays the page out at the width
/// asked, as a browser window that wide would (not a phone's zoomed-out view of a page with no viewport meta tag, which
/// hides what overflows). The page is given the command line's time: <see cref="SettleMilliseconds"/> of virtual time that
/// waits for its requests. A browser whose protocol is turned off (a machine's policy) is remembered, and captured from the
/// command line from then on.
/// </remarks>
internal static class ChromiumDevTools
{
    /// <summary>The virtual time a page is given before its picture, by the protocol and by <c>--virtual-time-budget</c>.</summary>
    internal const int SettleMilliseconds = 3000;

    /// <summary>The longest wait for the browser to open its DevTools port: a policy that turns it off never opens it.</summary>
    private static readonly TimeSpan PortWait = TimeSpan.FromSeconds(8);

    /// <summary>The browsers whose DevTools port never opened in this process: captured from the command line.</summary>
    private static readonly ConcurrentDictionary<string, bool> NoProtocol = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The page at <paramref name="url"/> as a PNG of <paramref name="width"/> × <paramref name="height"/>, or <c>null</c> with
    /// why when the browser could not be driven — the caller then captures from the command line, in what is left of its budget.
    /// </summary>
    /// <param name="flags">The browser's own flags (profile, network cut), without a window size, a picture or an address.</param>
    internal static async Task<(byte[]? Png, string? Why)> CaptureAsync(string executable, IReadOnlyList<string> flags, string profile,
                                                                        string url, int width, int height, TimeSpan budget,
                                                                        CancellationToken ct)
    {
        if (NoProtocol.ContainsKey(executable)) return (null, "its DevTools port did not open earlier in this session");
        var psi = new System.Diagnostics.ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var flag in flags) psi.ArgumentList.Add(flag);
        // The window is wide enough for any width the browser accepts; the page's own viewport is set below.
        psi.ArgumentList.Add("--remote-debugging-port=0");
        psi.ArgumentList.Add($"--window-size={Math.Max(width, 800)},{height}");
        psi.ArgumentList.Add("about:blank");
        System.Diagnostics.Process? browser = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(budget);
        try
        {
            browser = ChildProcess.Start(psi);
            _ = browser.StandardOutput.ReadToEndAsync(CancellationToken.None);
            _ = browser.StandardError.ReadToEndAsync(CancellationToken.None);
            var port = await PortAsync(browser, profile, deadline.Token).ConfigureAwait(false);
            if (port is null)
            {
                NoProtocol[executable] = true;
                return (null, "the browser did not open its DevTools port");
            }
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, Proxy = NetworkProxy.Shared });
            var socketUrl = await PageSocketAsync(http, port, deadline.Token).ConfigureAwait(false);
            if (socketUrl is null) return (null, "the browser listed no page to drive");

            using var socket = new ClientWebSocket();
            socket.Options.Proxy = null;
            await socket.ConnectAsync(new Uri(socketUrl), deadline.Token).ConfigureAwait(false);
            var session = new Session(socket);
            await session.CallAsync("Emulation.setDeviceMetricsOverride",
                new { width, height, deviceScaleFactor = 1, mobile = false }, deadline.Token).ConfigureAwait(false);
            await session.CallAsync("Page.enable", new { }, deadline.Token).ConfigureAwait(false);
            // Paused until the navigation starts, then advanced while no request is pending: what --virtual-time-budget does.
            await session.CallAsync("Emulation.setVirtualTimePolicy", new { policy = "pause" }, deadline.Token).ConfigureAwait(false);
            await session.CallAsync("Page.navigate", new { url }, deadline.Token).ConfigureAwait(false);
            await session.CallAsync("Emulation.setVirtualTimePolicy",
                new { policy = "pauseIfNetworkFetchesPending", budget = SettleMilliseconds }, deadline.Token).ConfigureAwait(false);
            await session.WaitForAsync("Emulation.virtualTimeBudgetExpired", deadline.Token).ConfigureAwait(false);
            var shot = await session.CallAsync("Page.captureScreenshot", new { format = "png" }, deadline.Token).ConfigureAwait(false);
            return shot.TryGetProperty("data", out var data) && data.GetString() is { } base64
                ? (Convert.FromBase64String(base64), null)
                : (null, "the browser returned no picture");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, $"the browser did not finish the capture within {budget.TotalSeconds:0} s");
        }
        catch (Exception ex) when (ex is HttpRequestException or WebSocketException or JsonException or IOException
                                   or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException
                                   or FormatException or System.ComponentModel.Win32Exception)
        {
            return (null, "the browser's DevTools protocol failed: " + ex.Message);
        }
        finally
        {
            if (browser is not null)
            {
                // Gone before the caller removes its profile: a browser still exiting holds files in it.
                try { browser.Kill(entireProcessTree: true); browser.WaitForExit(5000); } catch { /* already gone */ }
                browser.Dispose();
            }
        }
    }

    /// <summary>The port the browser wrote in its profile once its DevTools endpoint listens; <c>null</c> when it exited, or
    /// did not open it within <see cref="PortWait"/>.</summary>
    /// <remarks>The file is written in two lines, the port then the browser's path: read once both are there, never a port
    /// cut while it is written.</remarks>
    private static async Task<string?> PortAsync(System.Diagnostics.Process browser, string profile, CancellationToken ct)
    {
        var file = Path.Combine(profile, "DevToolsActivePort");
        var until = DateTime.UtcNow + PortWait;
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            if (browser.HasExited) return null;
            try
            {
                if (File.Exists(file) && File.ReadAllLines(file) is [{ Length: > 0 } port, { Length: > 0 }, ..]) return port.Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* being written: read again */ }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>The page target's socket, asked again while the browser has not created its first tab yet.</summary>
    private static async Task<string?> PageSocketAsync(HttpClient http, string port, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            using var targets = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct).ConfigureAwait(false));
            foreach (var target in targets.RootElement.EnumerateArray())
                if (target.TryGetProperty("type", out var type) && type.GetString() == "page"
                    && target.TryGetProperty("webSocketDebuggerUrl", out var socket) && socket.GetString() is { } url)
                    return url;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>One page's protocol session: calls answered by their id, events kept until waited for.</summary>
    private sealed class Session(ClientWebSocket socket)
    {
        private int _next;
        private readonly HashSet<string> _events = new(StringComparer.Ordinal);
        private readonly byte[] _buffer = new byte[16 * 1024];
        private readonly MemoryStream _message = new();

        public async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken ct)
        {
            var id = ++_next;
            var request = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
            await socket.SendAsync(request, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
            while (true)
            {
                using var message = await ReceiveAsync(ct).ConfigureAwait(false);
                var root = message.RootElement;
                if (root.TryGetProperty("id", out var answered) && answered.GetInt32() == id)
                {
                    if (root.TryGetProperty("error", out var error))
                        throw new InvalidOperationException($"{method}: {error.GetRawText()}");
                    return root.TryGetProperty("result", out var result) ? result.Clone() : default;
                }
                if (root.TryGetProperty("method", out var name) && name.GetString() is { } eventName) _events.Add(eventName);
            }
        }

        public async Task WaitForAsync(string eventName, CancellationToken ct)
        {
            while (!_events.Contains(eventName))
            {
                using var message = await ReceiveAsync(ct).ConfigureAwait(false);
                if (message.RootElement.TryGetProperty("method", out var name) && name.GetString() is { } seen) _events.Add(seen);
            }
        }

        private async Task<JsonDocument> ReceiveAsync(CancellationToken ct)
        {
            _message.SetLength(0);
            while (true)
            {
                var part = await socket.ReceiveAsync(_buffer, ct).ConfigureAwait(false);
                if (part.MessageType == WebSocketMessageType.Close) throw new WebSocketException("the browser closed its DevTools connection");
                _message.Write(_buffer, 0, part.Count);
                if (part.EndOfMessage) break;
            }
            return JsonDocument.Parse(_message.ToArray());
        }
    }
}
