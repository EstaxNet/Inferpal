using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Inferpal.Host.Acp;
using Inferpal.Localization;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace Inferpal.Host.SettingsWindow;

/// <summary>
/// <c>Inferpal.Host --settings [folder]</c>: Inferpal's settings in a browser window — the very form VS Code shows (its
/// webview bundle, its stylesheet, the same host answering it), for the editors that have no settings panel of their
/// own: an Agent Client Protocol client (Zed, the JetBrains IDEs, Neovim, Emacs).
/// </summary>
/// <remarks>
/// <para>
/// The page is served on the loopback address, under a random token; it calls a <see cref="HostServer"/> started for
/// <c>folder</c> as an ACP session starts one, through <c>POST &lt;page&gt;/rpc</c>. The bridge between the form and
/// the host is the one VS Code runs (<c>vscode/src/settingsBridge.ts</c>), bundled into the page.
/// </para>
/// <para>
/// ⚠ Any page the browser opens can send a request to a port on this machine. What stands between them and the host:
/// the token (in the path, never guessed), the <c>Host</c> header (a site that resolves its own name to 127.0.0.1 —
/// DNS rebinding — sends ITS name), the <c>Origin</c> of a request that has one, and <see cref="FormMethods"/>: the page
/// reaches the settings and nothing else — never a turn, a command, a tool.
/// </para>
/// </remarks>
internal sealed class SettingsWindowServer : IAsyncDisposable
{
    /// <summary>What the page may ask the host: the calls of the settings form (<c>settingsStandaloneShell.ts</c>).</summary>
    internal static readonly IReadOnlySet<string> FormMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "config/get", "config/update", "models/listByKind", "connection/check", "settings/strings", "settings/schema",
        "mcp/cards", "mcp/retry", "mcp/authorize", "permissions/table", "permissions/newRule",
        "settings/indexCard", "settings/indexRebuild", "settings/exclusions", "settings/docsSites", "settings/docsAction",
        "settings/contextUsage", "settings/projectFiles", "settings/repoInstructions", "settings/pinSizes",
        "settings/loadedModels", "settings/unloadModels", "settings/suggestModels",
    };

    /// <summary>The page's one call answered here rather than by the host: open a file in the system's application.</summary>
    internal const string OpenMethod = "gui/open";

    /// <summary>The files the page loads, from the web folder beside the executable.</summary>
    private static readonly IReadOnlyDictionary<string, string> WebFiles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["settings.css"]            = "text/css; charset=utf-8",
        ["settings-standalone.css"] = "text/css; charset=utf-8",
        ["settings-standalone.js"]  = "text/javascript; charset=utf-8",
    };

    /// <summary>How long the page may stay silent before it is taken for closed: a background tab's timers are slowed to
    /// one a minute by the browser, so this is well above it.</summary>
    internal static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(90);

    /// <summary>After the page said it closes: room for a reload, whose new page speaks at once.</summary>
    internal static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(3);

    /// <summary>How long the first visit is waited for.</summary>
    internal static readonly TimeSpan FirstVisitLimit = TimeSpan.FromMinutes(10);

    private readonly HostServer _host;
    private readonly JsonRpc _hostSide;
    private readonly JsonRpc _link;
    private readonly HttpListener _listener;
    private readonly string _token;
    private readonly int _port;
    private readonly string _webDir;
    private readonly Task _accepting;
    private long _lastHeard;
    private int _closing;

    /// <summary>The pages open now — each tab its own id — and when each last said so.</summary>
    /// <remarks>⚠ One page was assumed: <c>/settings</c> opens a new tab each time, and the close of the old tab — the
    /// last message heard — ended the server under the one still open, whose next sign of life came too late (every five
    /// seconds, once a minute in the background). Its next save then failed.</remarks>
    private readonly ConcurrentDictionary<string, long> _pages = new(StringComparer.Ordinal);

    /// <summary>What the page says once its server has stopped: the way to open it again, from where it was opened.</summary>
    private readonly string _gone;

    /// <summary>The page's address, its token included.</summary>
    public string Url => $"http://127.0.0.1:{_port}/{_token}/";

    /// <summary>Whether the page has been opened at least once.</summary>
    public bool Visited => Interlocked.Read(ref _lastHeard) != 0;

    private SettingsWindowServer(HostServer host, JsonRpc hostSide, JsonRpc link, HttpListener listener, int port, string webDir,
                                 string gone)
    {
        _gone     = gone;
        _host     = host;
        _hostSide = hostSide;
        _link     = link;
        _listener = listener;
        _port     = port;
        _webDir   = webDir;
        _token    = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _accepting = Task.Run(AcceptAsync);
    }

    /// <summary>The host for <paramref name="folder"/>, initialized as an ACP session's, and the page's server, listening.</summary>
    /// <param name="gone">What the page says once the server has stopped; default: run <c>--settings</c> again. A window an
    /// agent opened for <c>/settings</c> names <c>/settings</c>: its executable sits in the client's folders.</param>
    public static async Task<SettingsWindowServer> StartAsync(string folder, string webDir, Func<HostServer> hostFactory,
                                                              CancellationToken ct, string? gone = null)
    {
        var (hostStream, linkStream) = FullDuplexStream.CreatePair();
        var host     = hostFactory();
        var hostSide = HostRpc.Create(hostStream, hostStream, host);
        host.Attach(hostSide);
        hostSide.StartListening();
        // No target on this side: a request the host makes of an editor (an approval) finds nobody, and is refused.
        var link = HostRpc.Create(linkStream, linkStream);
        link.StartListening();
        try
        {
            await link.InvokeWithParameterObjectAsync<JsonElement>("initialize", new
            {
                rootDir       = folder,
                clientName    = "settings-window",
                editor        = "Inferpal",
                editorSurface = false,
                secrets       = false,
            }, ct);
            var (listener, port) = Listen();
            return new SettingsWindowServer(host, hostSide, link, listener, port, webDir, gone ?? Strings.SettingsWindowGone);
        }
        catch
        {
            link.Dispose();
            hostSide.Dispose();
            host.Dispose();
            throw;
        }
    }

    /// <summary>A free loopback port, taken: asked of the system, then bound — retried if another process took it between.</summary>
    private static (HttpListener Listener, int Port) Listen()
    {
        for (var attempt = 0; ; attempt++)
        {
            int port;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try { port = ((IPEndPoint)probe.LocalEndpoint).Port; }
            finally { probe.Stop(); }

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return (listener, port);
            }
            catch (HttpListenerException) when (attempt < 10)
            {
                listener.Close();
            }
        }
    }

    /// <summary>
    /// Until the page is closed: silent past <see cref="SilenceLimit"/>, or <see cref="CloseGrace"/> after it said it
    /// closes — or never opened within <see cref="FirstVisitLimit"/>.
    /// </summary>
    public async Task WaitForCloseAsync(CancellationToken ct, Func<DateTime>? clock = null)
    {
        clock ??= static () => DateTime.UtcNow;
        var started = clock();
        while (!ct.IsCancellationRequested)
        {
            var heard = Interlocked.Read(ref _lastHeard);
            var now = clock();
            // A page silent past the limit is gone (closed without a word); while one is open, the server stays.
            foreach (var (page, at) in _pages)
                if (now - new DateTime(at, DateTimeKind.Utc) > SilenceLimit) _pages.TryRemove(page, out _);
            if (!_pages.IsEmpty)
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(500), ct); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            if (heard == 0 ? now - started > FirstVisitLimit
                           : now - new DateTime(heard, DateTimeKind.Utc) > (Volatile.Read(ref _closing) == 1 ? CloseGrace : SilenceLimit))
                return;
            try { await Task.Delay(TimeSpan.FromMilliseconds(500), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>A page says it is open (<paramref name="page"/>: its id), or that it closes.</summary>
    private void PageSaid(string page, bool closing)
    {
        if (closing) _pages.TryRemove(page, out _);
        else _pages[page] = DateTime.UtcNow.Ticks;
        Heard(closing: closing && _pages.IsEmpty);
    }

    private void Heard(bool closing)
    {
        Volatile.Write(ref _closing, closing ? 1 : 0);
        Interlocked.Exchange(ref _lastHeard, DateTime.UtcNow.Ticks);
    }

    private async Task AcceptAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    internal async Task HandleAsync(HttpListenerContext context)
    {
        var request  = context.Request;
        var response = context.Response;
        try
        {
            response.Headers["Cache-Control"] = "no-store";
            var origin = $"http://127.0.0.1:{_port}";
            if (!string.Equals(request.Headers["Host"], $"127.0.0.1:{_port}", StringComparison.OrdinalIgnoreCase)
                || request.Headers["Origin"] is { } from && !string.Equals(from, origin, StringComparison.OrdinalIgnoreCase))
            {
                await SendAsync(response, 403, "text/plain; charset=utf-8", "Forbidden");
                return;
            }
            var prefix = "/" + _token + "/";
            var path = request.Url?.AbsolutePath ?? string.Empty;
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
            {
                await SendAsync(response, 404, "text/plain; charset=utf-8", "Not found");
                return;
            }

            var rest = path[prefix.Length..];
            switch (request.HttpMethod, rest)
            {
                case ("GET", ""):
                    Heard(closing: false);
                    await SendAsync(response, 200, "text/html; charset=utf-8", Page());
                    return;
                case ("GET", _) when WebFiles.TryGetValue(rest, out var type):
                    var file = Path.Combine(_webDir, rest);
                    if (File.Exists(file)) await SendAsync(response, 200, type, await File.ReadAllBytesAsync(file));
                    else await SendAsync(response, 404, "text/plain; charset=utf-8", "Not found");
                    return;
                case ("POST", "rpc"):
                    Heard(closing: false);
                    var (status, body) = await RpcAsync(request);
                    await SendAsync(response, status, "application/json; charset=utf-8", body);
                    return;
                case ("POST", "alive"):
                case ("POST", "close"):
                    PageSaid(request.QueryString["page"] ?? string.Empty, closing: rest == "close");
                    await SendAsync(response, 204, null, []);
                    return;
                default:
                    await SendAsync(response, 404, "text/plain; charset=utf-8", "Not found");
                    return;
            }
        }
        catch (Exception ex)
        {
            Services.Diagnostics.Swallow("SettingsWindow.Request", ex);
            try { response.Abort(); } catch { }
        }
    }

    /// <summary>One call of the page: a settings method relayed to the host, or a file opened.</summary>
    internal async Task<(int Status, string Body)> RpcAsync(HttpListenerRequest request)
    {
        string text;
        using (var reader = new StreamReader(request.InputStream, Encoding.UTF8)) text = await reader.ReadToEndAsync();
        return await CallAsync(text);
    }

    /// <summary><paramref name="text"/> is <c>{ method, params }</c>; the answer is <c>{ result }</c> or <c>{ error: { message } }</c>.</summary>
    internal async Task<(int Status, string Body)> CallAsync(string text)
    {
        JsonElement call;
        try { call = JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException) { return Error(400, "The request is not JSON."); }
        var method = call.ValueKind == JsonValueKind.Object && call.TryGetProperty("method", out var m)
                     && m.ValueKind == JsonValueKind.String ? m.GetString()! : string.Empty;
        JsonElement? args = call.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object ? p : null;

        if (method == OpenMethod)
        {
            var target = args is { } a && a.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String
                ? path.GetString()! : string.Empty;
            if (!File.Exists(target) && !Directory.Exists(target)) return Error(404, Strings.SettingsWindowNotFound(target));
            // The application the system gives the file outlives this server: never bound to it.
            using var _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return (200, "{\"result\":null}");
        }
        if (!FormMethods.Contains(method)) return Error(403, $"'{method}' is not a call of the settings form.");

        try
        {
            var result = args is { } named
                ? await _link.InvokeWithParameterObjectAsync<JsonElement>(method, named)
                : await _link.InvokeAsync<JsonElement>(method);
            return (200, JsonSerializer.Serialize(new { result }));
        }
        catch (RemoteInvocationException ex) { return Error(500, ex.Message); }
        catch (Exception ex) when (ex is ConnectionLostException or ObjectDisposedException) { return Error(503, _gone); }
    }

    private static (int, string) Error(int status, string message) =>
        (status, JsonSerializer.Serialize(new { error = new { message } }));

    /// <summary>The page: the form's two stylesheets and its bundle, the texts the extension would give it.</summary>
    internal string Page()
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        // The texts VS Code's extension gives the form (its window.__l10n), in the interface's language.
        var l10n = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["Settings saved."]  = Strings.SettingsWindowSaved,
            ["Loading settings…"] = Strings.SettingsWindowLoading,
            ["Connected"]          = Strings.SettingsWindowConnected,
            ["Backend unreachable"] = Strings.SettingsWindowUnreachable,
            ["Refresh models"]     = Strings.SettingsWindowRefreshModels,
            ["Show all models"]    = Strings.SettingsWindowShowAllModels,
            ["No model listed — is the backend reachable?"] = Strings.ChatNoModelListed,
        });
        var page = JsonSerializer.Serialize(new
        {
            gone     = _gone,
            noPicker = Strings.SettingsWindowNoPicker,
            noXray   = Strings.SettingsWindowNoXray,
        });
        var language = Strings.UiCulture.TwoLetterISOLanguageName;
        var title    = WebUtility.HtmlEncode(Strings.SettingsWindowTitle);
        // ⚠ The JSON goes into a <script>: the serializer escapes '<' (<), so no "</script>" can end it early.
        return $"""
            <!DOCTYPE html>
            <html lang="{language}">
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy"
                  content="default-src 'none'; style-src 'self'; script-src 'self' 'nonce-{nonce}'; connect-src 'self'; img-src 'self' data:;">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>{title}</title>
            <link rel="stylesheet" href="settings-standalone.css">
            <link rel="stylesheet" href="settings.css">
            </head>
            <body>
            <div id="app"></div>
            <script nonce="{nonce}">window.__l10n = {l10n}; window.__inferpal = {page};</script>
            <script src="settings-standalone.js"></script>
            </body>
            </html>
            """;
    }

    private static Task SendAsync(HttpListenerResponse response, int status, string? type, string body) =>
        SendAsync(response, status, type, Encoding.UTF8.GetBytes(body));

    private static async Task SendAsync(HttpListenerResponse response, int status, string? type, byte[] body)
    {
        response.StatusCode = status;
        if (type is not null) response.ContentType = type;
        response.ContentLength64 = body.Length;
        if (body.Length > 0) await response.OutputStream.WriteAsync(body);
        response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        try { _listener.Stop(); _listener.Close(); } catch { }
        try { await _accepting; } catch { }
        _link.Dispose();
        _hostSide.Dispose();
        _host.Dispose();
    }

    // ── The command ─────────────────────────────────────────────────────────────

    /// <summary>Where the page's files are: <c>web</c> beside the executable.</summary>
    internal static string DefaultWebDir => Path.Combine(AppContext.BaseDirectory, "web");

    /// <summary>Whether <paramref name="webDir"/> holds every file the page loads.</summary>
    internal static bool HasItsFiles(string webDir) => WebFiles.Keys.All(name => File.Exists(Path.Combine(webDir, name)));

    /// <summary>Opens <paramref name="url"/> in the system's browser; <c>false</c> when none could be started.</summary>
    internal static bool OpenInBrowser(string url)
    {
        // The browser outlives this process: never bound to it.
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Services.Diagnostics.Swallow("SettingsWindow.OpenBrowser", ex);
            return false;
        }
    }

    /// <summary>
    /// The settings window, from a terminal: the address printed and opened, the process kept until the page closes.
    /// Exit code 0 when Inferpal is then set up (what an ACP client's terminal setup expects), 1 otherwise.
    /// </summary>
    /// <param name="webDir">Where the page's files are; default: <see cref="DefaultWebDir"/>.</param>
    public static async Task<int> RunAsync(string folder, bool openBrowser, TextWriter output, CancellationToken ct,
                                           Func<HostServer>? hostFactory = null, string? webDir = null)
    {
        webDir ??= DefaultWebDir;
        if (!HasItsFiles(webDir))
        {
            output.WriteLine(Strings.SettingsWindowFilesMissing(webDir));
            return 2;
        }
        folder = Path.GetFullPath(folder);
        if (!Directory.Exists(folder))
        {
            output.WriteLine(Strings.SettingsWindowNotFound(folder));
            return 2;
        }

        await using var server = await StartAsync(folder, webDir, hostFactory ?? (() => new HostServer()), ct);
        output.WriteLine(Strings.SettingsWindowOpen(server.Url, folder));
        if (openBrowser && !OpenInBrowser(server.Url)) output.WriteLine(Strings.SettingsWindowNoBrowser);
        await server.WaitForCloseAsync(ct);
        output.WriteLine(Strings.SettingsWindowClosed);
        return await AcpReadiness.IsReadyAsync(CancellationToken.None) ? 0 : 1;
    }
}
