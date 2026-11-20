using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Inferpal.Services.Documents;

/// <summary>A browser that can capture a page without a window: its name, its executable, the way it is driven.</summary>
internal sealed record CaptureBrowser(string Name, string Executable, CaptureEngine Engine);

/// <summary>How a <see cref="CaptureBrowser"/> is driven.</summary>
internal enum CaptureEngine
{
    /// <summary>Edge, Chrome, Chromium, Brave: driven through the DevTools protocol (<see cref="ChromiumDevTools"/>), else
    /// <c>--headless --screenshot</c>.</summary>
    Chromium,
    /// <summary>Firefox: <c>--headless --screenshot</c> with a throwaway profile.</summary>
    Firefox,
    /// <summary>macOS's own WebKit, in a view off screen, driven by <c>osascript</c> in JavaScript.</summary>
    WebKit,
}

/// <summary>
/// Renders a page of this machine into a PNG, at a given width, with the page cut off from the network: only the page's own
/// origin, on the loopback, is reached (pari 95).
/// </summary>
/// <remarks>
/// <para>
/// Every system: macOS renders through WebKit, which it ships (Safari's engine; Safari itself has no command-line capture and
/// nothing that cuts its network); elsewhere the browser present by default (Edge on Windows, Firefox on most Linux), then any
/// Chromium or Firefox found. None found: <see cref="Find"/> returns <c>null</c> and the caller says how to get one.
/// </para>
/// <para>
/// ⚠ The network is cut by the browser's own settings in a throwaway profile, never by trusting the page: Chromium and Firefox
/// send every address but the page's origin to a proxy that does not exist, WebKit blocks every load but the origin's by a
/// content rule (<see cref="CaptureAsync"/>). A page that reaches for a font, a script or an image of the Internet renders
/// without it. A file of the workspace is served on the loopback for the capture (<c>WorkspacePages</c>), never opened from
/// the disk.
/// </para>
/// </remarks>
internal static class PageCapture
{
    /// <summary>The longest one capture may take — a browser's first start on a cold disk included.</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    /// <summary>About the narrowest window a headless Chromium keeps from the command line, whatever width it is asked
    /// (<see cref="ChromiumDevTools"/>): a capture narrower than this, taken that way, says so.</summary>
    internal const int CommandLineNarrowest = 520;

    /// <summary>The part of <see cref="Budget"/> a Chromium capture through its DevTools protocol may take before the command
    /// line takes over with the rest.</summary>
    private static readonly TimeSpan ProtocolBudget = TimeSpan.FromSeconds(30);

    /// <summary>The viewport's height for a width: a phone's for a narrow one, a laptop's otherwise (the measured pair: 390 ×
    /// 844 and 1280 × 900).</summary>
    internal static int HeightFor(int width) => width <= 600 ? 844 : 900;

    /// <summary>The browser that captures here, in the order of the fiche; <c>null</c> when this machine has none.</summary>
    /// <param name="exists">Whether a file exists (a test's seam).</param>
    /// <param name="onPath">The executable of that name on the PATH (a test's seam).</param>
    internal static CaptureBrowser? Find(Func<string, bool>? exists = null, Func<string, string?>? onPath = null) =>
        FindAll(exists, onPath).FirstOrDefault();

    /// <summary>Every browser that can capture here, in the order <see cref="Find"/> takes them (one per engine and name).</summary>
    internal static IEnumerable<CaptureBrowser> FindAll(Func<string, bool>? exists = null, Func<string, string?>? onPath = null)
    {
        exists ??= File.Exists;
        onPath ??= Shell.ShellLauncher.FindOnPath;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, path, engine) in Candidates(onPath))
            if (path is not null && exists(path) && seen.Add(name)) yield return new CaptureBrowser(name, path, engine);
    }

    private static IEnumerable<(string Name, string? Path, CaptureEngine Engine)> Candidates(Func<string, string?> onPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return ("WebKit", "/usr/bin/osascript", CaptureEngine.WebKit);
            foreach (var (name, app, engine) in new[]
                     {
                         ("Google Chrome", "Google Chrome.app/Contents/MacOS/Google Chrome", CaptureEngine.Chromium),
                         ("Microsoft Edge", "Microsoft Edge.app/Contents/MacOS/Microsoft Edge", CaptureEngine.Chromium),
                         ("Chromium", "Chromium.app/Contents/MacOS/Chromium", CaptureEngine.Chromium),
                         ("Brave", "Brave Browser.app/Contents/MacOS/Brave Browser", CaptureEngine.Chromium),
                         ("Firefox", "Firefox.app/Contents/MacOS/firefox", CaptureEngine.Firefox),
                     })
                yield return (name, "/Applications/" + app, engine);
            yield break;
        }
        if (OperatingSystem.IsWindows())
        {
            string? Under(string variable, string relative) =>
                Environment.GetEnvironmentVariable(variable) is { Length: > 0 } dir ? Path.Combine(dir, relative) : null;
            yield return ("Microsoft Edge", Under("ProgramFiles(x86)", @"Microsoft\Edge\Application\msedge.exe"), CaptureEngine.Chromium);
            yield return ("Microsoft Edge", Under("ProgramFiles", @"Microsoft\Edge\Application\msedge.exe"), CaptureEngine.Chromium);
            yield return ("Google Chrome", Under("ProgramFiles", @"Google\Chrome\Application\chrome.exe"), CaptureEngine.Chromium);
            yield return ("Google Chrome", Under("LOCALAPPDATA", @"Google\Chrome\Application\chrome.exe"), CaptureEngine.Chromium);
            yield return ("Brave", Under("ProgramFiles", @"BraveSoftware\Brave-Browser\Application\brave.exe"), CaptureEngine.Chromium);
            yield return ("Chromium", Under("LOCALAPPDATA", @"Chromium\Application\chrome.exe"), CaptureEngine.Chromium);
            yield return ("Firefox", Under("ProgramFiles", @"Mozilla Firefox\firefox.exe"), CaptureEngine.Firefox);
            yield break;
        }
        // Linux and the other Unixes: Firefox first, the browser most distributions install.
        yield return ("Firefox", onPath("firefox"), CaptureEngine.Firefox);
        foreach (var (name, command) in new[]
                 {
                     ("Microsoft Edge", "microsoft-edge"), ("Google Chrome", "google-chrome"), ("Google Chrome", "google-chrome-stable"),
                     ("Chromium", "chromium"), ("Chromium", "chromium-browser"), ("Brave", "brave-browser"),
                 })
            yield return (name, onPath(command), CaptureEngine.Chromium);
    }

    /// <summary>A new folder for a browser's profile and pictures, removed by the caller.</summary>
    /// <remarks>⚠ A browser installed as a snap sees its own /tmp, not ours: its folder goes where both can reach.</remarks>
    internal static string WorkFolder(CaptureBrowser browser) =>
        SnapName(browser.Executable) is { } snap && UserFolders.Of(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home
            ? Directory.CreateDirectory(Path.Combine(home, "snap", snap, "common", "inferpal-" + Guid.NewGuid().ToString("N"))).FullName
            : Directory.CreateTempSubdirectory("inferpal-page-").FullName;

    /// <summary>A headless Chromium's flags for a page of this machine: a fresh profile in <paramref name="userData"/>, and
    /// no network but the page's own origin (<paramref name="host"/>:<paramref name="port"/>).</summary>
    internal static string[] ChromiumFlags(string userData, string host, string port) =>
    [
        "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
        "--disable-background-networking", "--disable-component-update", "--disable-sync",
        "--disable-extensions", "--disable-breakpad", "--disable-crash-reporter", $"--user-data-dir={userData}",
        // No such proxy, and the loopback itself sent to it (<-loopback>) but for the page's own origin.
        "--proxy-server=127.0.0.1:9", $"--proxy-bypass-list=<-loopback>;{host}:{port}",
        $"--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE {host}",
        "--force-webrtc-ip-handling-policy=disable_non_proxied_udp", "--webrtc-ip-handling-policy=disable_non_proxied_udp",
        // A dev server's own certificate (https://localhost:5001): only the page's origin is reachable.
        "--ignore-certificate-errors", "--hide-scrollbars",
    ];

    /// <summary>
    /// Renders <paramref name="url"/> — an http page of this machine — at <paramref name="width"/> pixels by
    /// <paramref name="browser"/>: the PNG, or why there is none (said in English, to the model), and what a PNG drawn
    /// otherwise than asked should say (<c>Note</c>): a Chromium captured from its command line lays a page narrower than
    /// <see cref="CommandLineNarrowest"/> out at that width, so a phone's layout may not be the one shown.
    /// </summary>
    /// <remarks>
    /// ⚠ The page reaches its OWN origin and nothing else: not the Internet, and not the other ports of this machine either —
    /// a local service that fetches for its caller (a model server pulling a model, a dev server's proxy route) would carry
    /// the page out. Every other address goes to a proxy that does not exist, names resolve to nothing but the page's host,
    /// and WebRTC (which no proxy carries) is off.
    /// </remarks>
    /// <param name="height">The viewport's height; by default the one a page is looked at with (<see cref="HeightFor"/>).</param>
    internal static async Task<(byte[]? Png, string? Why, string? Note)> CaptureAsync(CaptureBrowser browser, string url, int width,
                                                                                      CancellationToken ct, int? height = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var page) || (page.Scheme != Uri.UriSchemeHttp && page.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("A capture renders an http page of this machine.", nameof(url));
        var host = page.Host;
        var port = page.Port.ToString(CultureInfo.InvariantCulture);
        var viewport = height ?? HeightFor(width);
        var work = WorkFolder(browser);
        string? note = null;
        try
        {
            var png = Path.Combine(work, "page.png");
            var profile = Directory.CreateDirectory(Path.Combine(work, "profile")).FullName;
            var psi = new ProcessStartInfo(browser.Executable);
            var budget = Budget;
            var size = $"{width.ToString(CultureInfo.InvariantCulture)},{viewport.ToString(CultureInfo.InvariantCulture)}";
            switch (browser.Engine)
            {
                case CaptureEngine.Chromium:
                    // The page laid out at the exact width asked (ChromiumDevTools); a browser whose protocol does not
                    // answer is captured from the command line, whose window cannot be narrower than about 520 px.
                    var devTools = Directory.CreateDirectory(Path.Combine(work, "profile-devtools")).FullName;
                    // One budget for the capture: the protocol gets part of it, the command line what is left.
                    var started = Stopwatch.StartNew();
                    var (drawn, why) = await ChromiumDevTools.CaptureAsync(browser.Executable, ChromiumFlags(devTools, host, port), devTools,
                                                                           url, width, viewport, ProtocolBudget, ct);
                    if (drawn is not null) return (drawn, null, null);
                    Diagnostics.Record("PageCapture.DevTools", $"{browser.Name}: {why}; captured from the command line instead");
                    if (width < CommandLineNarrowest)
                        note = $"{browser.Name} could not be driven to lay the page out at {width} px ({why}), so it was " +
                               $"captured from its command line, whose window is at least about {CommandLineNarrowest} px wide: " +
                               $"the picture is cut at {width}, and what a {width} px screen lays out differently (a phone's " +
                               "menu, a narrow column) is not what it shows.";
                    budget = Budget - started.Elapsed;
                    foreach (var a in ChromiumFlags(profile, host, port).Concat([$"--virtual-time-budget={ChromiumDevTools.SettleMilliseconds}",
                                                             $"--screenshot={png}", $"--window-size={size}", url]))
                        psi.ArgumentList.Add(a);
                    break;
                case CaptureEngine.Firefox:
                    await File.WriteAllTextAsync(Path.Combine(profile, "user.js"), FirefoxPrefs(host, port), ct);
                    foreach (var a in new[] { "--headless", "--no-remote", "--profile", profile, "--screenshot", png,
                                              $"--window-size={size}", url })
                        psi.ArgumentList.Add(a);
                    break;
                case CaptureEngine.WebKit:
                    var script = Path.Combine(work, "capture.js");
                    await File.WriteAllTextAsync(script, WebKitScript, ct);
                    foreach (var a in new[] { "-l", "JavaScript", script, url, width.ToString(CultureInfo.InvariantCulture),
                                              viewport.ToString(CultureInfo.InvariantCulture), png,
                                              "^" + page.Scheme + "://" + System.Text.RegularExpressions.Regex.Escape(host) + ":" + port + "/" })
                        psi.ArgumentList.Add(a);
                    break;
            }
            var run = await ChildProcess.RunAsync(psi, budget, ct);
            if (run.TimedOut) return (null, $"{browser.Name} did not finish the capture within {Budget.TotalSeconds:0} s: it was stopped.", null);
            if (!File.Exists(png) || new FileInfo(png).Length == 0)
            {
                var said = (run.Stderr + "\n" + run.Stdout).Trim();
                return (null, $"{browser.Name} made no picture of the page (exit code {run.ExitCode})"
                            + (said.Length > 0 ? ": " + SafeTruncate.Truncate(said.Replace("\r\n", "\n"), 600) : "."), null);
            }
            return (await File.ReadAllBytesAsync(png, ct), null, note);
        }
        finally
        {
            // A browser's crash reporter may still hold the profile for a moment after the browser exits.
            for (var attempt = 0; attempt < 3 && Directory.Exists(work); attempt++)
            {
                try { Directory.Delete(work, recursive: true); }
                catch (IOException) { await Task.Delay(300, CancellationToken.None); }
                catch (UnauthorizedAccessException) { await Task.Delay(300, CancellationToken.None); }
            }
        }
    }

    /// <summary>The snap a browser runs as — <c>/snap/bin/firefox</c>, or Ubuntu's <c>/usr/bin/firefox</c>, a small script
    /// that starts the snap — else <c>null</c>.</summary>
    internal static string? SnapName(string executable)
    {
        if (executable.StartsWith("/snap/bin/", StringComparison.Ordinal)) return Path.GetFileName(executable);
        try
        {
            var file = new FileInfo(executable);
            if (!file.Exists || file.Length > 64 * 1024 || OperatingSystem.IsWindows()) return null;
            var text = File.ReadAllText(executable);
            return System.Text.RegularExpressions.Regex.Match(text, @"/snap/bin/([A-Za-z0-9-]+)") is { Success: true } m ? m.Groups[1].Value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Firefox's throwaway profile: every request through a proxy that does not exist, the loopback included
    /// (<c>allow_hijacking_localhost</c>), but the page's own origin; no DNS of its own, no WebRTC, no prefetching.</summary>
    private static string FirefoxPrefs(string host, string port) =>
        "user_pref(\"network.proxy.type\", 1);\n" +
        "user_pref(\"network.proxy.http\", \"127.0.0.1\");\nuser_pref(\"network.proxy.http_port\", 9);\n" +
        "user_pref(\"network.proxy.ssl\", \"127.0.0.1\");\nuser_pref(\"network.proxy.ssl_port\", 9);\n" +
        "user_pref(\"network.proxy.socks\", \"127.0.0.1\");\nuser_pref(\"network.proxy.socks_port\", 9);\n" +
        "user_pref(\"network.proxy.socks_remote_dns\", true);\n" +
        $"user_pref(\"network.proxy.no_proxies_on\", \"{host}:{port}\");\n" +
        "user_pref(\"network.proxy.allow_hijacking_localhost\", true);\n" +
        "user_pref(\"network.trr.mode\", 5);\nuser_pref(\"media.peerconnection.enabled\", false);\n" +
        "user_pref(\"network.predictor.enabled\", false);\nuser_pref(\"network.dns.disablePrefetch\", true);\n" +
        "user_pref(\"network.prefetch-next\", false);\nuser_pref(\"network.http.speculative-parallel-limit\", 0);\n" +
        "user_pref(\"browser.shell.checkDefaultBrowser\", false);\nuser_pref(\"datareporting.policy.dataSubmissionEnabled\", false);\n";

    /// <summary>
    /// macOS: the page in a WebKit view off screen, every load but the loopback's and the files' blocked by a content rule,
    /// then a snapshot of the viewport written as a PNG of exactly the width asked (a Retina screen's scale undone).
    /// Arguments: url, width, height, output path, the page's origin as a url-filter.
    /// </summary>
    internal const string WebKitScript = """
        ObjC.import('Cocoa');
        ObjC.import('WebKit');
        function pump(until, seconds) {
          const end = Date.now() + seconds * 1000;
          while (!until() && Date.now() < end)
            $.NSRunLoop.currentRunLoop.runUntilDate($.NSDate.dateWithTimeIntervalSinceNow(0.05));
          return until();
        }
        function run(argv) {
          const url = argv[0], width = Number(argv[1]), height = Number(argv[2]), out = argv[3], origin = argv[4];
          $.NSApplication.sharedApplication;
          // One rule per kind, no alternation (WebKit's url-filter has none): block all, then the page's own origin only.
          const rules = JSON.stringify([
            { trigger: { 'url-filter': '.*' }, action: { type: 'block' } },
            { trigger: { 'url-filter': origin }, action: { type: 'ignore-previous-rules' } },
            { trigger: { 'url-filter': '^about:' }, action: { type: 'ignore-previous-rules' } },
            { trigger: { 'url-filter': '^data:' }, action: { type: 'ignore-previous-rules' } },
            { trigger: { 'url-filter': '^blob:' }, action: { type: 'ignore-previous-rules' } },
          ]);
          let list = null, compiled = false, failure = '';
          $.WKContentRuleListStore.defaultStore.compileContentRuleListForIdentifierEncodedContentRuleListCompletionHandler(
            'inferpal-loopback-only', rules, (l, e) => { list = l; failure = e ? ObjC.unwrap(e.localizedDescription) : ''; compiled = true; });
          if (!pump(() => compiled, 20) || !list || list.isNil()) throw new Error('the content rule that cuts the network did not compile: ' + failure);
          const config = $.WKWebViewConfiguration.alloc.init;
          config.userContentController.addContentRuleList(list);
          const frame = $.NSMakeRect(0, 0, width, height);
          const view = $.WKWebView.alloc.initWithFrameConfiguration(frame, config);
          const win = $.NSWindow.alloc.initWithContentRectStyleMaskBackingDefer(frame, 0, $.NSBackingStoreBuffered, false);
          win.contentView = view;
          // On screen as far as WebKit knows (it paints only a view in a window that is), placed where no screen shows it.
          win.setFrameOrigin($.NSMakePoint(-20000, -20000));
          win.orderBack($());
          view.loadRequest($.NSURLRequest.requestWithURL($.NSURL.URLWithString(url)));
          const started = Date.now();
          pump(() => Date.now() - started > 300 && !view.isLoading, 30);
          pump(() => false, 0.5);
          let image = null, done = false, why = '';
          const snap = $.WKSnapshotConfiguration.alloc.init;
          const scale = ($.NSScreen.mainScreen && !$.NSScreen.mainScreen.isNil()) ? $.NSScreen.mainScreen.backingScaleFactor : 1;
          snap.snapshotWidth = $.NSNumber.numberWithDouble(width / scale);
          view.takeSnapshotWithConfigurationCompletionHandler(snap, (i, e) => { image = i; why = e ? ObjC.unwrap(e.localizedDescription) : ''; done = true; });
          if (!pump(() => done, 20) || !image || image.isNil()) throw new Error('WebKit gave no snapshot: ' + why);
          const rep = $.NSBitmapImageRep.imageRepWithData(image.TIFFRepresentation);
          const png = rep.representationUsingTypeProperties(4, $());   // NSBitmapImageFileTypePNG
          if (!png.writeToFileAtomically(out, true)) throw new Error('the picture could not be written');
          return 'ok';
        }
        """;
}
