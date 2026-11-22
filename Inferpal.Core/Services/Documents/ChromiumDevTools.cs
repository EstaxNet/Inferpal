using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace Inferpal.Services.Documents;

/// <summary>
/// A capture by a Chromium browser (Edge, Chrome) driven through its DevTools protocol, the page's viewport set to the exact
/// size asked, as the DevTools' device toolbar sets it.
/// </summary>
/// <remarks>
/// ⚠ On Windows and macOS, a headless Chromium keeps its window at least about 520 px wide whatever <c>--window-size</c> asks: from the command
/// line, a phone's 390 px is laid out at 496 and the picture cut at 390 — elements shown cut that a phone shows whole, a
/// phone's own breakage never shown. The protocol's <c>Emulation.setDeviceMetricsOverride</c> lays the page out at the width
/// asked, as a browser window that wide would (not a phone's zoomed-out view of a page with no viewport meta tag, which
/// hides what overflows). The page is given the command line's time: <see cref="SettleMilliseconds"/> of virtual time that
/// waits for its requests. A browser whose port does not open in time (a machine's policy turns the protocol off, or the
/// machine is busy) is captured from the command line for <see cref="NoProtocolFor"/>, then driven again: a busy stretch
/// does not lay every later phone capture out wide, and a policy costs one wait each time it is tried.
/// </remarks>
internal static class ChromiumDevTools
{
    /// <summary>The virtual time a page is given before its picture, by the protocol and by <c>--virtual-time-budget</c>.</summary>
    internal const int SettleMilliseconds = 3000;

    /// <summary>The longest wait for the browser to open its DevTools port before a capture: a policy that turns it off
    /// never opens it, and the capture goes on from the command line.</summary>
    internal static readonly TimeSpan CapturePortWait = TimeSpan.FromSeconds(8);

    /// <summary>When each browser's DevTools port last did not open in time, in this process: for
    /// <see cref="NoProtocolFor"/> after it, the browser is captured from the command line. Only the capture reads it — a
    /// browser that was slow is still driven by <c>interact_page</c>, and its port opening clears the time.</summary>
    private static readonly ConcurrentDictionary<string, DateTime> PortTimeouts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long a browser whose port did not open is captured from the command line before it is driven again.</summary>
    internal static readonly TimeSpan NoProtocolFor = TimeSpan.FromMinutes(10);

    /// <summary>Whether <paramref name="executable"/> is captured from the command line now (<see cref="PortTimeouts"/>).</summary>
    internal static bool NoProtocol(string executable, DateTime? now = null) =>
        PortTimeouts.TryGetValue(executable, out var at) && (now ?? DateTime.UtcNow) - at < NoProtocolFor;

    /// <summary><paramref name="executable"/>'s port did not open in time.</summary>
    internal static void PortTimedOut(string executable, DateTime? now = null) => PortTimeouts[executable] = now ?? DateTime.UtcNow;

    /// <summary><paramref name="executable"/>'s port opened: its protocol answers, whatever came before.</summary>
    internal static void PortOpened(string executable) => PortTimeouts.TryRemove(executable, out _);

    /// <summary>
    /// The page at <paramref name="url"/> as a PNG of <paramref name="width"/> × <paramref name="height"/>, or <c>null</c> with
    /// why when the browser could not be driven — the caller then captures from the command line, in what is left of its budget.
    /// </summary>
    /// <param name="flags">The browser's own flags (profile, network cut), without a window size, a picture or an address.</param>
    /// <param name="network">Fed with the page's requests when given (<see cref="PageNetwork"/>).</param>
    internal static async Task<(byte[]? Png, string? Why)> CaptureAsync(string executable, IReadOnlyList<string> flags, string profile,
                                                                        string url, int width, int height, TimeSpan budget,
                                                                        CancellationToken ct, PageNetwork? network = null)
    {
        if (NoProtocol(executable)) return (null, "its DevTools port did not open, a few minutes ago");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(budget);
        try
        {
            Action<DevToolsConnection, string, JsonElement>? onEvent = network is null ? null : (_, name, p) => network.OnEvent(name, p);
            var (browser, why, portTimedOut) = await LaunchAsync(executable, flags, profile, width, height, CapturePortWait,
                                                                 ["Emulation.virtualTimeBudgetExpired"], onEvent, deadline.Token).ConfigureAwait(false);
            if (portTimedOut) PortTimedOut(executable);
            if (browser is null) return (null, why);
            PortOpened(executable);
            using (browser)
            {
                var page = browser.Page;
                await page.CallAsync("Page.enable", new { }, deadline.Token).ConfigureAwait(false);
                if (network is not null) await page.CallAsync("Network.enable", new { }, deadline.Token).ConfigureAwait(false);
                // Paused until the navigation starts, then advanced while no request is pending: what --virtual-time-budget does.
                await page.CallAsync("Emulation.setVirtualTimePolicy", new { policy = "pause" }, deadline.Token).ConfigureAwait(false);
                var mark = page.Mark;
                await page.CallAsync("Page.navigate", new { url }, deadline.Token).ConfigureAwait(false);
                await page.CallAsync("Emulation.setVirtualTimePolicy",
                    new { policy = "pauseIfNetworkFetchesPending", budget = SettleMilliseconds }, deadline.Token).ConfigureAwait(false);
                await page.WaitForAsync("Emulation.virtualTimeBudgetExpired", mark, deadline.Token).ConfigureAwait(false);
                var shot = await page.CallAsync("Page.captureScreenshot", new { format = "png" }, deadline.Token).ConfigureAwait(false);
                if (shot.TryGetProperty("data", out var data) && data.GetString() is { } base64)
                {
                    // Only a capture that drew the picture vouches for its requests: one that fell back read a load it aborted.
                    if (network is not null) network.Read = true;
                    return (Convert.FromBase64String(base64), null);
                }
                return (null, "the browser returned no picture");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, $"the browser did not finish the capture within {budget.TotalSeconds:0} s");
        }
        catch (Exception ex) when (IsProtocolFailure(ex) || ex is DevToolsCallException)
        {
            return (null, "the browser's DevTools protocol failed: " + ex.Message);
        }
    }

    /// <summary>What a driven browser throws when it fails or exits: said, never a crash. An error ANSWER is not one
    /// (<see cref="DevToolsCallException"/>): the browser that sent it still runs.</summary>
    internal static bool IsProtocolFailure(Exception ex) =>
        ex is HttpRequestException or WebSocketException or JsonException or IOException or UnauthorizedAccessException
           or InvalidOperationException or KeyNotFoundException or FormatException or System.ComponentModel.Win32Exception;

    /// <summary>
    /// Starts <paramref name="executable"/> headless with <paramref name="flags"/> and connects to its page, its viewport set to
    /// <paramref name="width"/> × <paramref name="height"/>; <c>null</c> with why when its protocol did not answer.
    /// </summary>
    /// <param name="portWait">How long the browser may take to open its DevTools port.</param>
    /// <param name="watched">The events the connection counts (<see cref="DevToolsConnection.WaitForAsync"/>).</param>
    /// <param name="onEvent">The page's events, read as they come (<see cref="DevToolsConnection"/>).</param>
    /// <returns>The browser, or why not — and whether its port did not open within <paramref name="portWait"/> while it ran
    /// (a policy may have turned the protocol off), as opposed to a browser that exited.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> fired; the browser is gone.</exception>
    internal static async Task<(ChromiumBrowser? Browser, string? Why, bool PortTimedOut)> LaunchAsync(
        string executable, IReadOnlyList<string> flags, string profile, int width, int height, TimeSpan portWait,
        IReadOnlyCollection<string> watched, Action<DevToolsConnection, string, JsonElement>? onEvent, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var flag in flags) psi.ArgumentList.Add(flag);
        // The window is wide enough for any width the browser accepts; the page's own viewport is set below.
        psi.ArgumentList.Add("--remote-debugging-port=0");
        psi.ArgumentList.Add($"--window-size={Math.Max(width, 800)},{height}");
        psi.ArgumentList.Add("about:blank");
        var process = ChildProcess.Start(psi);
        DevToolsConnection? page = null;
        try
        {
            _ = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            _ = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var port = await PortAsync(process, profile, portWait, ct).ConfigureAwait(false);
            // From here on, the process stopped and watched is the browser that runs, never a launcher that handed off.
            process = Owned(process, profile);
            if (port is null)
            {
                var running = !process.HasExited;
                Stop(process);
                return running
                    ? (null, $"the browser did not open its DevTools port within {portWait.TotalSeconds:0} s", true)
                    : (null, "the browser exited as it started", false);
            }
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, Proxy = NetworkProxy.Shared });
            var socketUrl = await PageSocketAsync(http, port, ct).ConfigureAwait(false);
            if (socketUrl is null)
            {
                Stop(process);
                return (null, "the browser listed no page to drive", false);
            }
            page = await DevToolsConnection.ConnectAsync(socketUrl, watched, onEvent, ct).ConfigureAwait(false);
            await page.CallAsync("Emulation.setDeviceMetricsOverride",
                new { width, height, deviceScaleFactor = 1, mobile = false }, ct).ConfigureAwait(false);
            return (new ChromiumBrowser(process, page), null, false);
        }
        catch
        {
            page?.Dispose();
            Stop(Owned(process, profile));
            throw;
        }
    }

    /// <summary>The browser that runs on <paramref name="profile"/>: <paramref name="launched"/>, or — when it handed off
    /// (<see cref="HandedOff"/>) — the process that holds the profile, else nothing would stop it, nor say it is gone.</summary>
    private static System.Diagnostics.Process Owned(System.Diagnostics.Process launched, string profile)
    {
        if (!HandedOff(launched)) return launched;
        if (ProfileOwner(profile) is not { } real)
        {
            Diagnostics.Record("ChromiumDevTools.Owned", "a browser that handed off was not found holding its profile");
            return launched;
        }
        launched.Dispose();
        return real;
    }

    /// <summary>Kills the browser and waits for it: gone before the caller removes its profile, which it holds files in.</summary>
    internal static void Stop(System.Diagnostics.Process browser)
    {
        try { browser.Kill(entireProcessTree: true); browser.WaitForExit(5000); } catch { /* already gone */ }
        browser.Dispose();
    }

    /// <summary>The port the browser wrote in its profile once its DevTools endpoint listens; <c>null</c> when it exited, or
    /// did not open it within <paramref name="wait"/>.</summary>
    /// <remarks>The file is written in two lines, the port then the browser's path: read once both are there, never a port
    /// cut while it is written.</remarks>
    private static async Task<string?> PortAsync(System.Diagnostics.Process browser, string profile, TimeSpan wait, CancellationToken ct)
    {
        var file = Path.Combine(profile, "DevToolsActivePort");
        var until = DateTime.UtcNow + wait;
        var handOff = DateTime.UtcNow + HandOffGrace;
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            // A launcher that handed off has exited while its browser opens the port; one that exited otherwise is gone.
            if (browser.HasExited && !(HandedOff(browser) && (ProfileHeld(profile) || DateTime.UtcNow < handOff))) return null;
            try
            {
                if (File.Exists(file) && File.ReadAllLines(file) is [{ Length: > 0 } port, { Length: > 0 }, ..]) return port.Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* being written: read again */ }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>How long a launcher that exited may take to have its browser hold the profile.</summary>
    private static readonly TimeSpan HandOffGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Whether <paramref name="launched"/> exited by handing the run to a browser it started: Chrome and Edge on Windows may
    /// start their browser as a child and exit at once (code 0), the picture or the DevTools port coming from the child.
    /// </summary>
    /// <remarks>⚠ Judged on its exit alone, never as the browser's end: what decides is the profile the child holds
    /// (<see cref="ProfileHeld"/>).</remarks>
    internal static bool HandedOff(System.Diagnostics.Process launched)
    {
        try { return launched.HasExited && launched.ExitCode == 0; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Whether a Chromium still runs on <paramref name="profile"/>: it holds its lock file there while it does.</summary>
    internal static bool ProfileHeld(string profile)
    {
        var singleton = new FileInfo(Path.Combine(profile, "SingletonLock"));   // Linux and macOS: a link, gone at exit
        if (singleton.LinkTarget is not null || singleton.Exists) return true;
        var lockFile = Path.Combine(profile, "lockfile");                       // Windows: a file held open while it runs
        if (!File.Exists(lockFile)) return false;
        try
        {
            using (File.Open(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>
    /// Waits, after a launcher that handed off (<see cref="HandedOff"/>), for the browser it started to let its profile go —
    /// a capture from the command line is written by that browser, and only when it exits is its picture whole.
    /// </summary>
    /// <returns>Whether the browser let it go within <paramref name="wait"/>; when not, or when <paramref name="ct"/> fires,
    /// the browser is stopped (it would outlive the capture, holding its work folder).</returns>
    internal static async Task<bool> WaitForHandOffAsync(string profile, TimeSpan wait, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var seen = false;
        try
        {
            while (DateTime.UtcNow - started < wait)
            {
                if (ProfileHeld(profile)) seen = true;
                else if (seen || DateTime.UtcNow - started > HandOffGrace) return true;
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            if (ProfileOwner(profile) is { } stopped) Stop(stopped);
            throw;
        }
        if (ProfileOwner(profile) is { } left) Stop(left);
        return false;
    }

    /// <summary>
    /// The process that holds <paramref name="profile"/>'s lock file, as Windows' Restart Manager names it; <c>null</c> when
    /// none does, or elsewhere (no launcher hands off there).
    /// </summary>
    /// <remarks>Read without the browser's DevTools port: it names the browser that never opened it too.</remarks>
    internal static System.Diagnostics.Process? ProfileOwner(string profile)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var lockFile = Path.Combine(profile, "lockfile");
        if (!File.Exists(lockFile)) return null;
        if (RestartManager.RmStartSession(out var session, 0, new System.Text.StringBuilder(64)) != 0) return null;
        try
        {
            if (RestartManager.RmRegisterResources(session, 1, [lockFile], 0, null, 0, null) != 0) return null;
            uint count = 4, reasons = 0;
            var holders = new RestartManager.ProcessInfo[count];
            if (RestartManager.RmGetList(session, out _, ref count, holders, ref reasons) != 0) return null;
            for (var i = 0; i < count; i++)
            {
                try
                {
                    var process = System.Diagnostics.Process.GetProcessById(holders[i].Process.ProcessId);
                    // The same process the Restart Manager saw, never another that took its id since.
                    if (process.StartTime.ToFileTimeUtc() == ((long)holders[i].Process.StartHigh << 32 | holders[i].Process.StartLow)) return process;
                    process.Dispose();
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Gone since it was listed.
                }
            }
            return null;
        }
        finally
        {
            _ = RestartManager.RmEndSession(session);
        }
    }

    private static class RestartManager
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct UniqueProcess
        {
            public int ProcessId;
            public uint StartLow, StartHigh;   // a FILETIME: two DWORDs, 4-byte aligned (a long would pad the struct)
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct ProcessInfo
        {
            public UniqueProcess Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
            public int ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        internal static extern int RmStartSession(out uint session, int flags, System.Text.StringBuilder key);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        internal static extern int RmRegisterResources(uint session, uint files, string[] names, uint applications,
                                                       UniqueProcess[]? processes, uint services, string[]? serviceNames);

        [DllImport("rstrtmgr.dll")]
        internal static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[] holders,
                                             ref uint reasons);

        [DllImport("rstrtmgr.dll")]
        internal static extern int RmEndSession(uint session);
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
}

/// <summary>A headless Chromium and the connection to its page; disposing it kills the browser.</summary>
internal sealed class ChromiumBrowser(System.Diagnostics.Process process, DevToolsConnection page) : IDisposable
{
    public DevToolsConnection Page { get; } = page;

    /// <summary>Whether the browser still runs and its page still answers the connection.</summary>
    public bool Running
    {
        // A browser that handed off and was not found holding its profile (Owned) is judged by its page alone.
        get { try { return (!process.HasExited || ChromiumDevTools.HandedOff(process)) && !Page.Lost; } catch (InvalidOperationException) { return false; } }
    }

    public void Dispose()
    {
        Page.Dispose();
        ChromiumDevTools.Stop(process);
    }
}
