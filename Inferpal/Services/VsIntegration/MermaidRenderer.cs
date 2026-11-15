using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Inferpal.Services.VsIntegration;

/// <summary>What drawing a Mermaid diagram gave: the PNG and its size as shown, or why there is none.</summary>
/// <param name="Unavailable">Nothing to draw it with (no WebView2 runtime, or it did not start): the diagram stays code,
/// and that is said.</param>
/// <param name="Error">Mermaid's own reason, when it could not read the diagram.</param>
/// <param name="Limit">Inferpal's own limit the diagram met (too large, too slow to draw), said to the person as it is.</param>
internal sealed record MermaidDrawing(string? PngPath, int Width, int Height, string? Error, bool Unavailable = false, string? Limit = null);

/// <summary>The colours a diagram is drawn in: Mermaid's dark or light theme, or black and white for high contrast.</summary>
internal enum MermaidTheme { Dark, Light, Contrast }

/// <summary>
/// Draws the Mermaid diagrams of Visual Studio's chat. The chat is Remote UI XAML, which runs no script: a WebView2 kept in
/// a hidden window of this process — the WebView2 runtime is Windows', and Visual Studio requires it — renders the diagram
/// with the Mermaid shipped in the VSIX (third-party/mermaid), and a PNG of it, on the chat's code ground, is shown. The
/// first diagram pays the WebView2's start (about a second), the next ones a tenth of that; it is closed with the window or
/// after ten idle minutes.
/// </summary>
/// <remarks>
/// ⚠ The render page carries a content policy: without it a diagram's label naming a remote image goes to the network.
/// Mermaid runs in strict mode. The page is driven through WebView2's own DevTools channel, never a debugging port.
/// </remarks>
internal sealed class MermaidRenderer : IDisposable
{
    /// <summary>The one renderer of the extension's process.</summary>
    internal static MermaidRenderer Shared { get; } = new();

    private static readonly TimeSpan IdleStop = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StartBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DrawBudget = TimeSpan.FromSeconds(10);
    /// <summary>After the WebView2 failed to start, the time before it is tried again: not at every diagram.</summary>
    private static readonly TimeSpan StartRetry = TimeSpan.FromMinutes(5);
    /// <summary>The largest side drawn, in device-independent pixels; a larger diagram says so instead of a cut picture.</summary>
    internal const int MaxSide = 6000;
    private const string DirPrefix = "inferpal-mermaid-";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, MermaidDrawing> _drawn = new(StringComparer.Ordinal);
    private Dispatcher? _ui;
    private HwndSource? _window;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _web;
    private string? _dir;
    private MermaidTheme? _theme;
    private Timer? _idle;
    private DateTime _retryAfter = DateTime.MinValue;
    private bool _disposed;

    /// <summary>The diagram of <paramref name="code"/> in <paramref name="theme"/>'s colours on <paramref name="ground"/>
    /// (#RRGGBB); never throws but for <paramref name="ct"/>.</summary>
    public async Task<MermaidDrawing> DrawAsync(string code, MermaidTheme theme, string ground, CancellationToken ct)
    {
        var key = $"{theme}\n{ground}\n{code}";
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_drawn.TryGetValue(key, out var known)) return known;
            if (_disposed || DateTime.UtcNow < _retryAfter) return new(null, 0, 0, null, Unavailable: true);
            MermaidDrawing drawing;
            var started = false;
            try
            {
                if (!await EnsureStartedAsync(ct).ConfigureAwait(false))
                {
                    Stop();
                    _retryAfter = DateTime.UtcNow + StartRetry;
                    return new(null, 0, 0, null, Unavailable: true);
                }
                started = true;
                drawing = await RenderAsync(code, theme, ground, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (started)
            {
                // Past the drawing's budget: that diagram is said too slow, never "unavailable"; the page that may still be
                // laying it out is closed, the next diagram starts a fresh one.
                Stop();
                drawing = new(null, 0, 0, null, Limit: Localization.Strings.MermaidTooSlow((int)DrawBudget.TotalSeconds));
            }
            catch (Exception ex)
            {
                // The WebView2 did not start or stopped answering: this diagram stays code, said unavailable. A start that
                // threw waits like one that failed, not at every diagram.
                Diagnostics.Swallow("MermaidRenderer.Draw", ex);
                Stop();
                if (!started) _retryAfter = DateTime.UtcNow + StartRetry;
                return new(null, 0, 0, null, Unavailable: true);
            }
            if (_drawn.Count > 64) _drawn.Clear();
            _drawn[key] = drawing;
            return drawing;
        }
        finally
        {
            _idle?.Change(IdleStop, Timeout.InfiniteTimeSpan);
            _gate.Release();
        }
    }

    /// <summary>The WebView2 runtime's version on this machine, or null when there is none.</summary>
    internal static string? RuntimeVersion()
    {
        UseShippedLoader();
        try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            Diagnostics.Swallow("MermaidRenderer.Runtime", ex);
            return null;
        }
    }

    private static int _loaderSet;

    /// <summary>
    /// Points WebView2 at the native loader shipped in the VSIX (runtimes\win-x64 or win-arm64\native).
    /// ⚠ The installed extension has no deps.json: nothing resolves the runtimes\ layout, and a native library is looked
    /// for beside the assembly only.
    /// </summary>
    private static void UseShippedLoader()
    {
        if (Interlocked.Exchange(ref _loaderSet, 1) == 1) return;
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? "win-arm64" : "win-x64";
        var folder = Path.Combine(Path.GetDirectoryName(typeof(MermaidRenderer).Assembly.Location)!, "runtimes", arch, "native");
        if (!File.Exists(Path.Combine(folder, "WebView2Loader.dll"))) return;
        try { CoreWebView2Environment.SetLoaderDllFolderPath(folder); }
        catch (InvalidOperationException ex) { Diagnostics.Swallow("MermaidRenderer.Loader", ex); } // already loaded
    }

    /// <summary>The Mermaid shipped beside the extension.</summary>
    private static string MermaidScript() =>
        Path.Combine(Path.GetDirectoryName(typeof(MermaidRenderer).Assembly.Location)!, "mermaid", "mermaid.min.js");

    private async Task<bool> EnsureStartedAsync(CancellationToken ct)
    {
        if (_web is not null) return true;
        Stop();
        var script = MermaidScript();
        if (!File.Exists(script))
        {
            Diagnostics.Swallow("MermaidRenderer.Start", new FileNotFoundException("Mermaid is not beside the extension", script));
            return false;
        }
        if (RuntimeVersion() is null) return false;

        if (_dir is null || !Directory.Exists(_dir))
        {
            // Gone (swept, cleaned by hand): a new folder, and the drawings that lived in the old one are drawn again.
            SweepOldFolders();
            _dir = Directory.CreateTempSubdirectory(DirPrefix).FullName;
            _drawn.Clear();
        }
        var page = Path.Combine(_dir, "render.html");
        await File.WriteAllTextAsync(page, RenderPage(new Uri(script).AbsoluteUri), ct).ConfigureAwait(false);

        // A thread of its own with a message loop: WebView2 answers on the thread that created it.
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { ready.SetResult(Dispatcher.CurrentDispatcher); Dispatcher.Run(); })
        {
            IsBackground = true, Name = "Inferpal Mermaid",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        _ui = await ready.Task.ConfigureAwait(false);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(StartBudget);
        try
        {
            await OnUiAsync(async () =>
            {
                // Never shown: a popup out of the screen, kept off the taskbar, never activated.
                _window = new HwndSource(new HwndSourceParameters("Inferpal Mermaid")
                {
                    Width = 1600, Height = 1200, PositionX = -32000, PositionY = -32000,
                    WindowStyle = unchecked((int)0x80000000),          // WS_POPUP
                    ExtendedWindowStyle = 0x00000080 | 0x08000000,      // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                });
                var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_dir, "webview"));
                _controller = await environment.CreateCoreWebView2ControllerAsync(_window.Handle);
                _controller.Bounds = new System.Drawing.Rectangle(0, 0, 1600, 1200);
                _controller.IsVisible = true;
                var web = _controller.CoreWebView2;
                web.Settings.AreDevToolsEnabled = false;
                web.Settings.AreDefaultContextMenusEnabled = false;
                web.Settings.AreHostObjectsAllowed = false;
                web.Settings.IsWebMessageEnabled = false;
                web.Settings.IsStatusBarEnabled = false;
                var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                web.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
                web.Navigate(new Uri(page).AbsoluteUri);
                if (!await loaded.Task) throw new InvalidOperationException("the render page did not load");
                // Only for the thread still current: a start given up past its budget has been stopped meanwhile.
                if (_ui == Dispatcher.CurrentDispatcher) _web = web;
                else { try { _controller?.Close(); } catch { } }
            }, budget.Token).ConfigureAwait(false);
            await CallAsync("Emulation.setDeviceMetricsOverride", new { width = 1600, height = 1200, deviceScaleFactor = 2, mobile = false }, budget.Token).ConfigureAwait(false);
            // Ready only once Mermaid has loaded: a page without it would fail every diagram.
            while ((await EvaluateAsync("typeof window.renderDiagram === 'function' && typeof mermaid === 'object'", budget.Token).ConfigureAwait(false)).ValueKind != JsonValueKind.True)
                await Task.Delay(100, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Diagnostics.Swallow("MermaidRenderer.Start", new TimeoutException($"WebView2 did not get ready within {StartBudget.TotalSeconds} s"));
            _web = null;
            return false;
        }
        _theme = null;
        _idle ??= new Timer(_ => { if (_gate.Wait(0)) { try { Stop(); } finally { _gate.Release(); } } });
        return true;
    }

    private async Task<MermaidDrawing> RenderAsync(string code, MermaidTheme theme, string ground, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(DrawBudget);
        if (_theme != theme)
        {
            await EvaluateAsync($"setTheme('{theme.ToString().ToLowerInvariant()}')", budget.Token).ConfigureAwait(false);
            _theme = theme;
        }
        var result = await EvaluateAsync($"renderDiagram({JsonSerializer.Serialize(code)})", budget.Token, awaitPromise: true).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("the render page did not answer");
        if (!result.GetProperty("ok").GetBoolean())
            return new(null, 0, 0, result.GetProperty("error").GetString() is { Length: > 0 } why ? why : null,
                       Limit: result.GetProperty("error").GetString() is { Length: > 0 } ? null : Localization.Strings.MermaidBroken);
        int w = result.GetProperty("w").GetInt32(), h = result.GetProperty("h").GetInt32();
        if (w > MaxSide || h > MaxSide)
            return new(null, 0, 0, null, Limit: Localization.Strings.MermaidTooLarge(w, h, MaxSide));

        // Named by what it shows: a drawing already on disk (and maybe held by the chat's image) is never rewritten.
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{theme}\n{ground}\n{code}")))[..16] + ".png";
        var png = Path.Combine(_dir!, name);
        if (!File.Exists(png))
        {
            // On the chat's code ground, opaque: the picture opened larger reads in any viewer. A large diagram is taken
            // at one pixel per point instead of two: the picture stays a few megabytes, not hundreds.
            var (r, g, b) = Rgb(ground);
            var scale = (long)w * h > 2_000_000 ? 0.5 : 1;
            await CallAsync("Emulation.setDefaultBackgroundColorOverride", new { color = new { r, g, b, a = 1 } }, budget.Token).ConfigureAwait(false);
            var shot = await CallAsync("Page.captureScreenshot",
                new { format = "png", captureBeyondViewport = true, clip = new { x = 0, y = 0, width = w, height = h, scale } },
                budget.Token).ConfigureAwait(false);
            await File.WriteAllBytesAsync(png, Convert.FromBase64String(shot.GetProperty("data").GetString() ?? string.Empty), budget.Token).ConfigureAwait(false);
        }
        // Kept fresh while in use: the sweep of another window leaves a folder written to within the week.
        try { Directory.SetLastWriteTimeUtc(_dir!, DateTime.UtcNow); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return new(png, w, h, null);
    }

    /// <summary>#RRGGBB as its three components; black for anything else.</summary>
    internal static (int R, int G, int B) Rgb(string hex) =>
        hex is ['#', _, _, _, _, _, _] && int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var v)
            ? ((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF) : (0, 0, 0);

    private async Task<JsonElement> EvaluateAsync(string expression, CancellationToken ct, bool awaitPromise = false)
    {
        var result = await CallAsync("Runtime.evaluate", new { expression, awaitPromise, returnByValue = true }, ct).ConfigureAwait(false);
        return result.TryGetProperty("result", out var r) && r.TryGetProperty("value", out var v) ? v.Clone() : default;
    }

    /// <summary>One DevTools call through WebView2's own channel, on its thread; past <paramref name="ct"/> it is given up.</summary>
    private async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(parameters);
        string answer = string.Empty;
        await OnUiAsync(async () => answer = await (_web ?? throw new InvalidOperationException("no WebView2")).CallDevToolsProtocolMethodAsync(method, json), ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(answer);
        return doc.RootElement.Clone();
    }

    /// <summary>Runs <paramref name="work"/> on the WebView2's thread and waits for it, at most until <paramref name="ct"/>.</summary>
    private async Task OnUiAsync(Func<Task> work, CancellationToken ct)
    {
        var ui = _ui ?? throw new InvalidOperationException("no WebView2 thread");
        // VSTHRD001 is about Visual Studio's main thread; this dispatcher is the renderer's own thread, which VS never runs on.
#pragma warning disable VSTHRD001
        var operation = ui.InvokeAsync(work);
#pragma warning restore VSTHRD001
        await (await operation.Task.WaitAsync(ct).ConfigureAwait(false)).WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The render page: Mermaid from the VSIX, strict, at its natural size, and a content policy that lets nothing
    /// a diagram names reach the network.</summary>
    internal static string RenderPage(string mermaidUrl) => $$"""
        <!doctype html><html><head><meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src file: 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; font-src data:">
        <style>html,body{margin:0;background:transparent;font-family:'Segoe UI',sans-serif;font-size:13px;width:max-content}#d{display:inline-block;padding:8px}</style>
        <script src="{{mermaidUrl}}"></script></head><body><div id="d"></div><script>
        const natural = { useMaxWidth: false };
        const contrast = { background: '#000000', mainBkg: '#000000', primaryColor: '#000000', secondaryColor: '#000000',
          tertiaryColor: '#000000', primaryTextColor: '#FFFFFF', secondaryTextColor: '#FFFFFF', tertiaryTextColor: '#FFFFFF',
          textColor: '#FFFFFF', lineColor: '#FFFFFF', primaryBorderColor: '#FFFFFF', nodeBorder: '#FFFFFF',
          edgeLabelBackground: '#000000', clusterBkg: '#000000', clusterBorder: '#FFFFFF', noteBkgColor: '#000000',
          noteTextColor: '#FFFFFF', actorBkg: '#000000', actorTextColor: '#FFFFFF', actorBorder: '#FFFFFF', signalColor: '#FFFFFF',
          signalTextColor: '#FFFFFF', labelBoxBkgColor: '#000000', labelTextColor: '#FFFFFF' };
        window.setTheme = (kind) => mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', suppressErrorRendering: true,
          theme: kind === 'light' ? 'default' : kind === 'contrast' ? 'base' : 'dark', fontFamily: "'Segoe UI', sans-serif",
          themeVariables: Object.assign({ fontFamily: "'Segoe UI', sans-serif", fontSize: '13px' }, kind === 'contrast' ? contrast : {}),
          flowchart: natural, sequence: natural, class: natural, state: natural, er: natural, gantt: natural, pie: natural, journey: natural });
        let n = 0;
        window.renderDiagram = async (code) => {
          const d = document.getElementById('d'); d.textContent = '';
          try { const { svg } = await mermaid.render('m' + (++n), code); d.innerHTML = svg;
                const r = d.getBoundingClientRect(); return { ok: true, w: Math.ceil(r.width), h: Math.ceil(r.height) }; }
          catch (e) { document.getElementById('m' + n)?.remove(); document.getElementById('dm' + n)?.remove();
                      return { ok: false, error: String(e && e.message || e).split('\n')[0].replace(/[\s:]+$/, '').slice(0, 200) + '.' }; }
        };
        </script></body></html>
        """;

    /// <summary>The folders earlier sessions left (a crash, a profile still held): removed after a week untouched. A folder
    /// in use is touched at every drawing, so another window sweeping never takes it.</summary>
    private static void SweepOldFolders()
    {
        try
        {
            foreach (var old in Directory.EnumerateDirectories(Path.GetTempPath(), DirPrefix + "*"))
            {
                try
                {
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(old) > TimeSpan.FromDays(7))
                        Directory.Delete(old, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* held: the next sweep */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("MermaidRenderer.Sweep", ex);
        }
    }

    /// <summary>Closes the WebView2 (its processes leave with it), its window and its thread.</summary>
    private void Stop()
    {
        _web = null;
        var ui = _ui;
        _ui = null;
        if (ui is null) return;
        var controller = _controller;
        var window = _window;
        _controller = null;
        _window = null;
        try
        {
            // Not awaited: the renderer's own thread closes what it created, then ends (VSTHRD001 as in OnUiAsync).
#pragma warning disable VSTHRD001
            _ = ui.InvokeAsync(() =>
            {
                try { controller?.Close(); } catch { }
                try { window?.Dispose(); } catch { }
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            });
#pragma warning restore VSTHRD001
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("MermaidRenderer.Stop", ex);
        }
    }

    public void Dispose()
    {
        // Never waits on a drawing in progress: the window is closing, and that drawing goes with it.
        var held = _gate.Wait(0);
        try
        {
            _disposed = true;
            _idle?.Dispose();
            Stop();
            // The drawings' folder, when the WebView2 has let go of its profile; otherwise a later sweep takes it.
            if (_dir is not null)
            {
                try { Directory.Delete(_dir, recursive: true); } catch { }
            }
        }
        finally
        {
            if (held) _gate.Release();
        }
    }
}
