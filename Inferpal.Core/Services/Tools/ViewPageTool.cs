using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Inferpal.Services.Documents;

namespace Inferpal.Services.Tools;

/// <summary>
/// Agent tool: looks at a page of the app being built — served on this machine, or an HTML file of the workspace — as a
/// browser renders it, at a laptop's width and a phone's (pari 95). What the source cannot show is on the picture: a button
/// white on white, a component wider than a phone, a banner over a form, a total cut off.
/// </summary>
/// <remarks>
/// <para>
/// The pictures reach the model the way <c>read_file</c>'s images do (<see cref="ToolImages"/>): as pictures for a model that
/// sees, as the text read on them by OCR for one that does not.
/// </para>
/// <para>
/// ⚠ This machine only, network cut (<see cref="PageCapture"/>): a page of the Internet is <c>fetch_url</c>'s. ⚠ Opening a
/// page runs its scripts, which may call the app (a GET that changes data): a card first, as <c>http_request</c> asks — one
/// "Always" per local address, the same agreement for both tools; for a file of the workspace, one per workspace.
/// </para>
/// <para>
/// ⚠ A file of the workspace is served over the loopback by <see cref="WorkspacePages"/>, never opened as a <c>file:</c> URL:
/// a page opened from the disk can frame any other file of the machine — one the ignore files keep from the model, a key
/// outside the workspace — and put it on the picture the model reads. Served over http, it reaches only what the server
/// gives: the workspace's files, minus the excluded ones.
/// </para>
/// </remarks>
internal sealed class ViewPageTool : ITool
{
    private readonly IApprovalService _approval;
    private readonly Func<string> _getWorkspaceRoot;
    private readonly ToolImageInbox _images;
    private readonly Func<IReadOnlyList<CaptureBrowser>> _findBrowsers;

    public ViewPageTool(IApprovalService approval, Func<string> getWorkspaceRoot, ToolImageInbox images,
                        Func<IReadOnlyList<CaptureBrowser>>? findBrowsers = null)
    {
        _approval = approval;
        _getWorkspaceRoot = getWorkspaceRoot;
        _images = images;
        _findBrowsers = findBrowsers ?? (() => [.. PageCapture.FindAll()]);
    }

    public string Name => "view_page";

    /// <inheritdoc/>
    /// <remarks>A browser runs the page's scripts, which may call the app: what they change is beyond the call.</remarks>
    public ToolEffect EffectOf(JsonElement args) => ToolEffect.Network | ToolEffect.Runs;

    internal const int DefaultWide = 1280, DefaultNarrow = 390, MinWidth = 320, MaxWidth = 2560;

    public string Description =>
        "Look at a page of the app you are building as a browser renders it — served on this machine " +
        "(http://localhost:5173/cart) or an HTML file of the workspace — captured at a laptop's width (1280 px) and a phone's " +
        "(390 px), or at the one width you give. Use it for what the source does not show: a button that cannot be seen, a " +
        "layout that overflows on a phone, an element covering another, text cut off. Pages of this machine only; the page " +
        "reaches nothing but its own address. The person approves the first look at each local address.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            url   = new { type = "string",  description = "A page served on this machine: http://localhost:<port>/<path> (or 127.0.0.1)." },
            path  = new { type = "string",  description = "Or an HTML file of the workspace, relative to its root (when nothing serves it)." },
            width = new { type = "integer", description = $"One width in CSS pixels ({MinWidth}-{MaxWidth}); without it, both {DefaultWide} and {DefaultNarrow}." },
        },
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var url = args.Str("url");
        var path = args.Str("path");
        if (string.IsNullOrWhiteSpace(url) == string.IsNullOrWhiteSpace(path))
            return "Error: give either url (a page served on this machine) or path (an HTML file of the workspace), not both.";

        string target, shown, subject, grantKey, details, askedAs;
        string? servedRoot = null;
        if (!string.IsNullOrWhiteSpace(url))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return $"Error: '{url}' is not an http or https URL. An HTML file of the workspace goes in path.";
            // ⚠ localhost or a loopback address, never a name under .localhost: http_request forces such a name onto the
            // loopback, a browser asks the system's resolver, which may answer with another machine.
            if (!HttpRequestTool.IsLoopback(uri) || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
                return $"Error: '{uri.Host}' is not this machine: view_page looks at pages served here (localhost, 127.0.0.1). " +
                       "Read a page of the Internet with fetch_url.";
            target = uri.AbsoluteUri;
            shown = target;
            // The same subject, rules and agreement as http_request's GET: the person said yes (or no) to this app once, for
            // both tools — asked as http_request, after view_page's own rules had their say.
            subject = HttpRequestTool.Subject("GET", uri);
            grantKey = $"http_request {HttpRequestTool.Origin(uri)}";
            askedAs = "http_request";
            details = Localization.Strings.ViewPageCardServed(target);
        }
        else
        {
            var root = servedRoot = _getWorkspaceRoot();
            var file = PathSanitizer.Sanitize(path, root);
            PathSanitizer.AssertUnderRoot(file, root);
            if (Directory.Exists(file)) return $"'{Path.GetFileName(file)}' is a folder: name an HTML file in it.";
            if (!File.Exists(file)) return ToolFailure.Said(Localization.Strings.ToolFileNotFound(file));
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is not (".html" or ".htm" or ".xhtml" or ".svg"))
                return $"'{Path.GetFileName(file)}' is not a page (HTML or SVG): read it with read_file.";
            shown = Path.GetRelativePath(root, file).Replace('\\', '/');
            target = shown;   // resolved against the server's address once it listens
            subject = file;
            grantKey = $"{Name} workspace files";
            askedAs = Name;
            details = Localization.Strings.ViewPageCardFile(shown);
        }

        var widths = new List<int>();
        string? widthNote = null;
        if (args.Has("width"))
        {
            var (width, note) = ClampedArgument.Read(args, "width", DefaultWide, MinWidth, MaxWidth);
            widths.Add(width);
            widthNote = note;
        }
        else widths.AddRange([DefaultWide, DefaultNarrow]);

        var browsers = _findBrowsers();
        if (browsers.Count == 0)
            return "No browser on this machine can capture a page: install Firefox or Google Chrome, then call view_page again.";

        if (_approval.Refuses(Name, subject)) return ToolFailure.CancelledByUser;
        if (!await _approval.RequestScopedApprovalAsync(askedAs, details, subject, grantKey, ct)) return ToolFailure.CancelledByUser;

        // The root the person approved the page under, never one read again after the card.
        using var served = servedRoot is null ? null : WorkspacePages.Start(servedRoot);
        if (served is not null) target = served.UrlOf(target);
        try
        {
            return await CaptureAllAsync(browsers, target, shown, widths, widthNote, ct);
        }
        catch (ArgumentException ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>Each width by the first browser that captures it; one that fails hands over to the next (WebKit on a macOS
    /// that refuses it, a snap that cannot write where it is told), and the ones that failed are named.</summary>
    private async Task<string> CaptureAllAsync(IReadOnlyList<CaptureBrowser> browsers, string target, string shown, List<int> widths,
                                               string? widthNote, CancellationToken ct)
    {
        var taken = new List<string>();
        var failed = new List<string>();
        var stem = Path.GetFileNameWithoutExtension(new Uri(target).Segments.LastOrDefault()?.Trim('/') is { Length: > 0 } s ? s : "page");
        var next = 0;
        CaptureBrowser? browser = null;
        foreach (var width in widths)
        {
            byte[]? png = null;
            while (png is null && next < browsers.Count)
            {
                browser = browsers[next];
                (png, var why) = await PageCapture.CaptureAsync(browser, target, width, ct);
                if (png is null) { failed.Add($"by {browser.Name} at {width} px: {why}"); next++; }
            }
            if (png is null || browser is null) break;
            var read = ChatImages.Read(png, $"{stem}-{width}px.png");
            if (read.Image is not { } image) { failed.Add($"at {width} px: {read.Refusal}"); continue; }
            _images.Put(image);
            taken.Add($"{width}×{PageCapture.HeightFor(width).ToString(CultureInfo.InvariantCulture)}");
        }

        var lines = new List<string>();
        if (widthNote is not null) lines.Add(widthNote);
        if (taken.Count > 0)
            lines.Add($"{shown} captured by {browser!.Name}, cut off from the network, at {string.Join(" and ", taken)}: " +
                      $"the picture{(taken.Count > 1 ? "s follow" : " follows")} this result. Only the first screen of the page " +
                      "is captured; what the page loads from anywhere but its own address (the Internet, another port of " +
                      "this machine) is missing on purpose.");
        lines.AddRange(failed.Select(f => $"Not captured {f}"));
        if (taken.Count == 0) lines.Add("If the page is served by an app that is starting, wait for it (run_command action='wait') and call again.");
        return string.Join("\n", lines);
    }
}

/// <summary>
/// The workspace's files served on a loopback port for the browser that captures one of them: GET and HEAD, inside the
/// root only, and never a file the ignore files keep from the model (<see cref="ContentExclusions"/>).
/// </summary>
internal sealed class WorkspacePages : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _root;
    private readonly int _port;

    private WorkspacePages(HttpListener listener, string root, int port) => (_listener, _root, _port) = (listener, root, port);

    /// <summary>Listens on a free loopback port and serves until disposed.</summary>
    public static WorkspacePages Start(string root)
    {
        for (var attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/");
            try { listener.Start(); }
            catch (HttpListenerException) when (attempt < 4) { listener.Close(); continue; }   // taken between the probe and here
            var pages = new WorkspacePages(listener, root, port);
            _ = pages.ServeAsync();
            return pages;
        }
    }

    /// <summary>The address of <paramref name="relative"/> (a path under the root, '/'-separated) on this server.</summary>
    public string UrlOf(string relative) =>
        $"http://127.0.0.1:{_port.ToString(CultureInfo.InvariantCulture)}/"
        + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Task.Run(() => AnswerAsync(context));
        }
    }

    /// <summary>The file a request names, or <c>null</c> when it is outside the root, excluded, or no file.</summary>
    internal string? FileFor(string absolutePath)
    {
        try
        {
            var relative = Uri.UnescapeDataString(absolutePath).TrimStart('/');
            if (relative.Length == 0 || relative.Contains('\0')) return null;
            var file = PathSanitizer.Sanitize(relative, _root);
            PathSanitizer.AssertUnderRoot(file, _root);
            if (!File.Exists(file) || ContentExclusions.Of(file) is not null) return null;
            return file;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private async Task AnswerAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var method = context.Request.HttpMethod;
            var file = method is "GET" or "HEAD" ? FileFor(context.Request.Url?.AbsolutePath ?? "") : null;
            if (file is null)
            {
                response.StatusCode = method is "GET" or "HEAD" ? 404 : 405;
                return;
            }
            response.ContentType = TypeOf(file);
            response.Headers["Cache-Control"] = "no-store";
            var bytes = await File.ReadAllBytesAsync(file);
            response.ContentLength64 = bytes.Length;
            if (method == "GET") await response.OutputStream.WriteAsync(bytes);
        }
        catch (Exception ex) when (ex is IOException or HttpListenerException or UnauthorizedAccessException or ObjectDisposedException)
        {
            try { response.StatusCode = 500; } catch { /* headers already sent */ }
        }
        finally
        {
            try { response.Close(); } catch { /* the browser went away */ }
        }
    }

    private static string TypeOf(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".xhtml" => "application/xhtml+xml",
        ".css" => "text/css; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".json" or ".map" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { /* already closed */ }
    }
}
