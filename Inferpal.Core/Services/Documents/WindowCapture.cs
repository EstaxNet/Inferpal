using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Inferpal.Services.Documents;

/// <summary>One window of an app the agent launched: its picture (when one could be taken) and its accessibility tree.</summary>
internal sealed record CapturedWindow(string Title, int ProcessId, byte[]? Png, int Width, int Height, IReadOnlyList<string> Tree)
{
    /// <summary>A file dialog (open, save): its contents are the person's folders and files, so neither its picture nor its
    /// tree is given, and no step acts in it.</summary>
    public bool FileDialog { get; init; }
}

/// <summary>What a look at a process tree gave: its windows, or why there is none to give.</summary>
internal sealed record WindowReading(IReadOnlyList<CapturedWindow> Windows, string? Refusal, IReadOnlyList<string> Notes);

/// <summary>
/// The windows of a process tree — an app the agent launched as a background job and the processes it started (<c>dotnet run</c>
/// starts the app as its child) — captured, and read as a screen reader reads them (role, name, state, value, place). Never a
/// window of another process: the windows are chosen by their owner's process id.
/// </summary>
/// <remarks>
/// <para>Windows: Windows PowerShell 5.1 (the probe's script, <c>docs/probes/pari102/view.ps1</c>, made a product's) —
/// <c>PrintWindow</c> renders a window even under another one, UI Automation reads WPF, WinForms, MAUI and WinUI alike.
/// ⚠ A process's parent id is never updated when the parent dies, and ids are reused: a child counts only if it was born after
/// its parent — without that, a job given the dead parent's id of the desktop shell took in every app the person opened.</para>
/// <para>macOS: <c>screencapture -l</c> and System Events, behind permissions the system asks for the app that hosts Inferpal
/// (Screen Recording, Accessibility, Automation): missing, it is said with the gesture, never a blank picture shown as a window.
/// ⚠ A title is the app's text: it is never trusted as a separator, and every window found is checked against the tree again.</para>
/// <para>Linux: X11 (or XWayland) through <c>xprop</c> and <c>xwd</c> (decoded here), the tree through AT-SPI in
/// <c>python3</c>. ⚠ Without a compositor, <c>xwd</c> gives what the SCREEN shows where the window is — another window over
/// it included: no picture then, said. A native Wayland window cannot be captured without the portal's dialog: said.</para>
/// </remarks>
internal static class WindowCapture
{
    /// <summary>What a look may take beyond the wait for a first window: the pictures and the trees.</summary>
    internal static readonly TimeSpan Margin = TimeSpan.FromSeconds(60);

    /// <summary>The most nodes a window's tree gives; past it, the cut is said in the tree.</summary>
    internal const int MaxNodes = 400;

    /// <summary>Looks at the windows of <paramref name="rootPid"/>'s process tree, waiting up to <paramref name="wait"/> for
    /// a first one to appear.</summary>
    internal static async Task<WindowReading> ReadAsync(int rootPid, TimeSpan wait, CancellationToken ct)
    {
        var dir = Directory.CreateTempSubdirectory("inferpal-window-").FullName;
        try
        {
            if (OperatingSystem.IsWindows()) return await WindowsAsync(rootPid, wait, dir, ct);
            if (OperatingSystem.IsMacOS()) return await MacAsync(rootPid, wait, dir, ct);
            if (OperatingSystem.IsLinux()) return await LinuxAsync(rootPid, wait, dir, ct);
            return new([], "this system has no way to look at a window", []);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // A tool of the system that is not there (ps on a minimal image): named, not a failure with no cause.
            return new([], "a program this needs could not start: " + ex.Message, []);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temporary folder: best-effort cleanup */ }
        }
    }

    // ── Windows ──

    private static async Task<WindowReading> WindowsAsync(int rootPid, TimeSpan wait, string dir, CancellationToken ct)
    {
        var psi = ScriptProcess.WindowsPowerShell(dir, "view.ps1", WindowsScript,
                        "-RootPid", rootPid.ToString(CultureInfo.InvariantCulture), "-OutDir", dir,
                        "-WaitSeconds", ((int)wait.TotalSeconds).ToString(CultureInfo.InvariantCulture),
                        "-MaxNodes", MaxNodes.ToString(CultureInfo.InvariantCulture));
        var budget = wait + Margin;
        var run = await ChildProcess.RunAsync(psi, budget, ct);
        var windows = new List<CapturedWindow>();
        var notes = new List<string>();
        foreach (var line in run.Stdout.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('{')))
        {
            if (Parse(line) is { } window) windows.Add(window);
            else if (ErrorOf(line) is { } error) notes.Add("a window could not be read: " + error);
        }
        if (run.TimedOut)
            notes.Add($"the look was stopped after {budget.TotalSeconds:0} s: windows after the last one shown may be missing");
        else if (run.ExitCode != 0)
        {
            var why = SafeTruncate.Truncate(run.Stderr.Trim(), 400);
            if (windows.Count == 0) return new([], "the look at the windows failed: " + why, []);
            notes.Add("the look stopped before its end, windows may be missing: " + why);
        }
        return new(windows, null, notes);
    }

    /// <summary>One window as the scripts write it: <c>{"pid","title","png","width","height","tree":[…]}</c>, the picture a
    /// file of the temporary folder; <c>null</c> for any other line.</summary>
    internal static CapturedWindow? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("pid", out var pid) || !pid.TryGetInt32(out var id)) return null;
            var png = r.TryGetProperty("png", out var p) && p.ValueKind == JsonValueKind.String && File.Exists(p.GetString())
                ? File.ReadAllBytes(p.GetString()!) : null;
            var tree = r.TryGetProperty("tree", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Select(e => e.GetString() ?? "").ToList() : [];
            return new(r.TryGetProperty("title", out var ti) && ti.ValueKind == JsonValueKind.String ? ti.GetString() ?? "" : "", id,
                       png is { Length: > 0 } ? png : null,
                       r.TryGetProperty("width", out var w) && w.TryGetInt32(out var wi) ? wi : 0,
                       r.TryGetProperty("height", out var h) && h.TryGetInt32(out var hi) ? hi : 0, tree)
            {
                FileDialog = r.TryGetProperty("fileDialog", out var f) && f.ValueKind == JsonValueKind.True,
            };
        }
        catch (JsonException) { return null; }
    }

    /// <summary>A window whose tree shows a file dialog — AT-SPI's file chooser, the column browser of a macOS open or save
    /// panel — kept by its title alone, without picture or tree.</summary>
    /// <remarks>shortcut: the macOS panel is told by its AXBrowser, measured on no Mac; a panel shown in list view only
    /// would pass — upgrade with a measure on macOS.</remarks>
    internal static CapturedWindow AsFileDialog(CapturedWindow window) =>
        window.Tree.Any(line => line.TrimStart() is var l && (l.StartsWith("FileChooser", StringComparison.Ordinal)
                                                              || (OperatingSystem.IsMacOS() && l.StartsWith("Browser", StringComparison.Ordinal))))
            ? window with { Png = null, Tree = [], FileDialog = true }
            : window;

    /// <summary>A script's <c>{"error": "…"}</c> line: a window it could not read.</summary>
    private static string? ErrorOf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var e)
                ? SafeTruncate.Truncate(e.GetString() ?? "", 300) : null;
        }
        catch (JsonException) { return null; }
    }

    // ── macOS ──

    private static async Task<WindowReading> MacAsync(int rootPid, TimeSpan wait, string dir, CancellationToken ct)
    {
        var script = Path.Combine(dir, "windows.js");
        await File.WriteAllTextAsync(script, MacScript, ct);
        var notes = new List<string>();
        var deadline = DateTime.UtcNow + wait;
        List<(int Id, int Pid, string Title, int Width, int Height)> found;
        var screen = true;
        while (true)
        {
            var pids = (await TreeOfAsync(rootPid, ct)).ToHashSet();
            var run = await ChildProcess.RunAsync(ScriptProcess.Start("/usr/bin/osascript", "-l", "JavaScript", script, "list",
                                                        string.Join(',', pids)), Margin, ct);
            if (run.TimedOut || run.ExitCode != 0)
                return new([], "the list of windows could not be read: "
                               + (run.TimedOut ? "osascript did not answer" : SafeTruncate.Truncate(run.Stderr.Trim(), 300)), []);
            found = [];
            foreach (var line in run.Stdout.Split('\n').Select(l => l.TrimEnd('\r')))
            {
                if (line == "NO_SCREEN_RECORDING") { screen = false; continue; }
                // The script puts no tab and no line break in a title: five fields, or the line is not a window.
                var parts = line.Split('\t');
                if (parts is [var id, var pid, var w, var h, var title] && int.TryParse(id, out var i) && int.TryParse(pid, out var p)
                    && pids.Contains(p)
                    && double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
                    && double.TryParse(h, NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
                    found.Add((i, p, title, (int)width, (int)height));
            }
            if (found.Count > 0 || DateTime.UtcNow > deadline) break;
            await Task.Delay(500, ct);
        }
        if (!screen)
            notes.Add("macOS has not let the app that runs Inferpal record the screen: the windows are not shown. The person can "
                    + "allow it in System Settings → Privacy & Security → Screen & System Audio Recording, then restart that app.");
        var windows = new List<CapturedWindow>();
        foreach (var (id, pid, title, width, height) in found)
        {
            byte[]? png = null;
            var fileDialog = false;
            if (screen)
            {
                var file = Path.Combine(dir, $"window{id}.png");
                var shot = await ChildProcess.RunAsync(ScriptProcess.Start("/usr/sbin/screencapture", "-x", "-o", "-l" + id.ToString(CultureInfo.InvariantCulture), file), Margin, ct);
                png = File.Exists(file) && new FileInfo(file).Length > 0 ? await File.ReadAllBytesAsync(file, ct) : null;
                if (png is null) notes.Add($"the window \"{title}\" could not be captured: " + Why(shot));
            }
            var tree = await ChildProcess.RunAsync(ScriptProcess.Start("/usr/bin/osascript", "-l", "JavaScript", script, "tree",
                                                         pid.ToString(CultureInfo.InvariantCulture), title,
                                                         MaxNodes.ToString(CultureInfo.InvariantCulture)), Margin, ct);
            var lines = tree.Stdout.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
            switch (lines)
            {
                case ["NO_ACCESSIBILITY"]:
                    lines = [];
                    Once(notes, "Accessibility", "macOS has not let the app that runs Inferpal read other apps' controls: the "
                        + "accessibility tree is not given. The person can allow it in System Settings → Privacy & Security → Accessibility.");
                    break;
                case ["FILE_DIALOG"]:
                    lines = [];
                    png = null;
                    fileDialog = true;
                    break;
                case ["NO_AUTOMATION"]:
                    lines = [];
                    Once(notes, "Automation", "macOS has not let the app that runs Inferpal ask System Events for the controls: the "
                        + "accessibility tree is not given. The person can allow it in System Settings → Privacy & Security → Automation.");
                    break;
                case [var only] when only.StartsWith("TREE_ERROR ", StringComparison.Ordinal):
                    lines = [];
                    notes.Add($"the controls of \"{title}\" could not be read: {only["TREE_ERROR ".Length..]}");
                    break;
                case [] when tree.TimedOut || tree.ExitCode != 0:
                    notes.Add($"the controls of \"{title}\" could not be read: " + Why(tree));
                    break;
            }
            windows.Add(fileDialog ? new(title, pid, null, 0, 0, []) { FileDialog = true }
                                   : AsFileDialog(new(title, pid, png, width, height, lines)));
        }
        return new(windows, null, notes);
    }

    // ── Linux ──

    private static async Task<WindowReading> LinuxAsync(int rootPid, TimeSpan wait, string dir, CancellationToken ct)
    {
        var notes = new List<string>();
        var x11 = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));
        var wayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        if (!x11 && !wayland)
            return new([], "there is no display here (neither DISPLAY nor WAYLAND_DISPLAY is set): no window can be looked at", []);
        var missing = new[] { "xprop", "xwd" }.Where(t => Shell.ShellLauncher.FindOnPath(t) is null).ToList();
        var python = Shell.ShellLauncher.FindOnPath("python3");
        var windows = new List<CapturedWindow>();
        // The X windows that are dialogs: a dialog whose controls could not be read may be a file dialog, and is not shown.
        var dialogs = new List<int>();
        if (x11 && missing.Count == 0)
        {
            var host = Environment.MachineName;
            var deadline = DateTime.UtcNow + wait;
            List<(string Id, int Pid, string Title, bool FileDialog, bool Dialog)> found;
            var elsewhere = 0;
            var listed = true;
            while (true)
            {
                var pids = (await TreeOfAsync(rootPid, ct)).ToHashSet();
                found = [];
                elsewhere = 0;
                var list = await ChildProcess.RunAsync(ScriptProcess.Start("xprop", "-root", "_NET_CLIENT_LIST"), Margin, ct);
                if (list.TimedOut || list.ExitCode != 0)
                    return new([], "the list of windows could not be read from the X server: " + Why(list), []);
                // No window manager publishing its clients (WSLg's, a bare X server): no X window can be found this way.
                listed = !list.Stdout.Contains("no such atom", StringComparison.Ordinal);
                if (!listed) break;
                foreach (var id in list.Stdout.Split('#').Skip(1).SelectMany(s => s.Split(',')).Select(s => s.Trim()).Where(s => s.StartsWith("0x")))
                {
                    var props = await ChildProcess.RunAsync(ScriptProcess.Start("xprop", "-id", id, "_NET_WM_PID", "WM_CLIENT_MACHINE", "_NET_WM_NAME",
                                                                  "WM_WINDOW_ROLE", "_NET_WM_WINDOW_TYPE"), Margin, ct);
                    string? Value(string name) => props.Stdout.Split('\n')
                        .FirstOrDefault(l => l.StartsWith(name, StringComparison.Ordinal) && l.Contains('='))?.Split('=', 2)[1].Trim();
                    if (!int.TryParse(Value("_NET_WM_PID"), out var pid) || !pids.Contains(pid)) continue;
                    // A process id is the client's word, and means nothing on another machine: a remote client's window is not ours.
                    if (Value("WM_CLIENT_MACHINE") is { } machine && !string.Equals(machine.Trim('"'), host, StringComparison.OrdinalIgnoreCase))
                    {
                        elsewhere++;
                        continue;
                    }
                    var name = Value("_NET_WM_NAME") is { } quoted && quoted.Length >= 2 && quoted[0] == '"'
                        ? quoted[1..^1].Replace("\\\"", "\"") : "";
                    // GTK names its file dialogs' role; a dialog of any other kind is told by its window type.
                    found.Add((id, pid, name, Value("WM_WINDOW_ROLE")?.Contains("FileChooser", StringComparison.Ordinal) == true,
                               Value("_NET_WM_WINDOW_TYPE")?.Contains("_NET_WM_WINDOW_TYPE_DIALOG", StringComparison.Ordinal) == true));
                }
                if (found.Count > 0 || DateTime.UtcNow > deadline) break;
                await Task.Delay(500, ct);
            }
            if (elsewhere > 0) notes.Add($"{elsewhere} window(s) of another machine claiming a process of the job left out");
            if (found.Count == 0 && !listed)
                notes.Add("no picture: the X server's window manager does not list its windows (_NET_CLIENT_LIST), as WSLg's does "
                        + "not — only the accessibility tree is given");
            else if (found.Count == 0 && wayland)
                notes.Add("no X window of the job: a native Wayland window cannot be captured without the desktop's own dialog — only "
                        + "its accessibility tree is given");
            var composited = found.Count > 0 && python is not null && await ComposedAsync(python, dir, ct);
            if (found.Count > 0 && !composited)
                notes.Add("no picture: no compositor could be seen running, and without one a window's picture holds whatever covers "
                        + "it on the screen — another app's window included");
            foreach (var (id, pid, title, fileDialog, dialog) in found)
            {
                // ⚠ A file dialog is never captured: its picture is the person's folders.
                if (fileDialog)
                {
                    windows.Add(new(title, pid, null, 0, 0, []) { FileDialog = true });
                    continue;
                }
                if (dialog) dialogs.Add(windows.Count);
                byte[]? png = null;
                int width = 0, height = 0;
                if (composited)
                {
                    var file = Path.Combine(dir, "window.xwd");
                    var dump = await ChildProcess.RunAsync(ScriptProcess.Start("sh", "-c", "xwd -silent -id \"$1\" > \"$2\"", "sh", id, file), Margin, ct);
                    (png, width, height) = File.Exists(file) ? Xwd.ToPng(await File.ReadAllBytesAsync(file, ct)) : (null, 0, 0);
                    if (png is null) notes.Add($"the window \"{title}\" could not be captured: " + Why(dump));
                }
                windows.Add(new(title, pid, png, width, height, []));
            }
        }
        else if (x11) notes.Add($"the windows are not shown: {string.Join(" and ", missing)} {(missing.Count == 1 ? "is" : "are")} not installed "
                              + "(the package x11-apps or x11-utils, xorg-x11-apps on Fedora)");
        else notes.Add("a native Wayland window cannot be captured without the desktop's own dialog: only its accessibility tree is given");

        // The trees, by AT-SPI: one run for every window of the tree.
        if (python is null) notes.Add("the accessibility tree is not given: python3 is not installed");
        else
        {
            var script = Path.Combine(dir, "atspi.py");
            await File.WriteAllTextAsync(script, LinuxTreeScript, ct);
            var pids = await TreeOfAsync(rootPid, ct);
            var run = await ChildProcess.RunAsync(ScriptProcess.Start(python, script, string.Join(',', pids), MaxNodes.ToString(CultureInfo.InvariantCulture)), Margin, ct);
            if (run.Stdout.Trim() == "NO_ATSPI")
                notes.Add("the accessibility tree is not given: AT-SPI is not reachable from python3 (the package python3-gi and "
                        + "gir1.2-atspi-2.0, and the desktop's accessibility bus)");
            else
            {
                if (run.TimedOut || run.ExitCode != 0) notes.Add("the accessibility trees may be incomplete: " + Why(run));
                foreach (var line in run.Stdout.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('{')))
                    if (Parse(line) is { } tree)
                    {
                        // The window of the same title first; a window of that process with no tree yet only then.
                        var i = windows.FindIndex(w => w.ProcessId == tree.ProcessId && w.Title == tree.Title && w.Tree.Count == 0);
                        if (i < 0) i = windows.FindIndex(w => w.ProcessId == tree.ProcessId && w.Tree.Count == 0 && w.Title.Length == 0);
                        if (i >= 0) windows[i] = AsFileDialog(windows[i] with { Tree = tree.Tree });
                        else windows.Add(AsFileDialog(tree));
                    }
            }
        }
        var unread = 0;
        foreach (var i in dialogs.Where(i => windows[i].Tree.Count == 0 && windows[i].Png is not null))
        {
            windows[i] = windows[i] with { Png = null };
            unread++;
        }
        if (unread > 0)
            notes.Add($"{unread} dialog window(s) not shown: their controls could not be read, and a dialog may be a file dialog, "
                      + "whose picture is the person's folders");
        return new(windows, null, notes);
    }

    /// <summary>Whether a compositor runs on the X display (GDK asks the server who owns <c>_NET_WM_CM_S0</c>); not known is
    /// no.</summary>
    private static async Task<bool> ComposedAsync(string python, string dir, CancellationToken ct)
    {
        var script = Path.Combine(dir, "composited.py");
        await File.WriteAllTextAsync(script, "import gi\ngi.require_version('Gdk', '3.0')\nfrom gi.repository import Gdk\n"
                                             + "print('YES' if Gdk.Screen.get_default().is_composited() else 'NO')\n", ct);
        var run = await ChildProcess.RunAsync(ScriptProcess.Start(python, script), Margin, ct);
        return run.Stdout.Trim() == "YES";
    }

    /// <summary>The process ids of <paramref name="rootPid"/> and every process under it, by <c>ps</c> (macOS, Linux, where a
    /// process whose parent died is given a new parent: a stale parent id cannot pull another tree in).</summary>
    internal static async Task<IReadOnlyList<int>> TreeOfAsync(int rootPid, CancellationToken ct)
    {
        var run = await ChildProcess.RunAsync(ScriptProcess.Start("ps", "-A", "-o", "pid=", "-o", "ppid="), Margin, ct);
        var children = new Dictionary<int, List<int>>();
        foreach (var line in run.Stdout.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts is [var a, var b] && int.TryParse(a, out var pid) && int.TryParse(b, out var parent))
                (children.TryGetValue(parent, out var l) ? l : children[parent] = []).Add(pid);
        }
        var tree = new List<int> { rootPid };
        for (var i = 0; i < tree.Count; i++)
            if (children.TryGetValue(tree[i], out var kids)) tree.AddRange(kids.Where(k => !tree.Contains(k)));
        return tree;
    }

    private static string Why(ChildProcessResult run) =>
        run.TimedOut ? "it did not answer in time" : $"exit code {run.ExitCode}" + (run.Stderr.Trim() is { Length: > 0 } e ? ": " + SafeTruncate.Truncate(e, 300) : "");

    private static void Once(List<string> notes, string key, string note)
    {
        if (!notes.Any(n => n.Contains(key, StringComparison.Ordinal))) notes.Add(note);
    }

    /// <summary>What every Windows script of the job's windows starts with: UI Automation, the window finder
    /// (<c>InferpalWin.Windows</c>) and the job's process tree (<c>Get-Tree</c>, a child only if born after its parent) — one
    /// reader for looking at the windows and acting in them (<see cref="WindowActions"/>).</summary>
    internal const string WindowsSetup = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
        Add-Type @'
        using System;
        using System.Collections.Generic;
        // The type opens on the line of its last using, and RECT on the line of a member: a line that starts with a type
        // declaration is read as one of this file's own by the readers of C# declarations (project map, impact).
        using System.Runtime.InteropServices; public static class InferpalWin {
            public delegate bool EnumProc(IntPtr h, IntPtr l);
            [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
            [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
            [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
            [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
            [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);
            [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
            [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
            [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
            [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
            [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
            [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
            [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int size);
            [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT v, int size); public struct RECT { public int Left, Top, Right, Bottom; }
            public static List<IntPtr> Windows(HashSet<uint> pids) {
                var found = new List<IntPtr>();
                EnumWindows((h, l) => {
                    uint pid; GetWindowThreadProcessId(h, out pid);
                    int cloaked; DwmGetWindowAttribute(h, 14, out cloaked, 4);
                    // Top-level, or owned with a title (a dialog, a message box) — not a tooltip or a menu.
                    bool shown = GetWindow(h, 4) == IntPtr.Zero || GetWindowTextLength(h) > 0;
                    if (pids.Contains(pid) && IsWindowVisible(h) && shown && cloaked == 0) found.Add(h);
                    return true;
                }, IntPtr.Zero);
                return found;
            }
        }
        '@
        [InferpalWin]::SetProcessDPIAware() | Out-Null

        # The top-level window an element belongs to, by handle (0 for none): the root of its nearest native window, asked of
        # the system when it is used. ⚠ UI Automation lists an owned dialog UNDER its owner: a control of the dialog is in the
        # owner's tree too, whether or not the dialog was among the windows listed.
        function Root-Of($el) {
            $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
            $e = $el
            while ($null -ne $e) {
                $h = [int64]$e.Current.NativeWindowHandle
                if ($h -ne 0) { return [InferpalWin]::GetAncestor([IntPtr]$h, 2).ToInt64() }
                $e = $walker.GetParent($e)
            }
            return [int64]0
        }

        # A file dialog — the file name box (Vista and later), the shell's view of a folder, the classic folder picker (a
        # dialog box holding a tree view): its contents are the person's folders and files, never shown, never acted in.
        # Its own markers only, never those of a dialog it owns (Root-Of). shortcut: the dialogs Windows ships; a toolkit's
        # own file picker drawn without them is not told — upgrade when one is met.
        function Is-FileDialog($w) {
            $AE = [System.Windows.Automation.AutomationElement]
            $scope = [System.Windows.Automation.TreeScope]::Descendants
            $own = [int64]$w.Current.NativeWindowHandle
            $markers = [System.Windows.Automation.OrCondition]::new([System.Windows.Automation.Condition[]]@(
                (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'FileNameControlHost')),
                (New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'DUIViewWndClassName')),
                (New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'SHELLDLL_DefView')),
                (New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'SHBrowseForFolder ShellNameSpace Control'))))
            foreach ($m in $w.FindAll($scope, $markers)) { if ((Root-Of $m) -eq $own) { return $true } }
            if ($w.Current.ClassName -eq '#32770') {
                $tree = New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'SysTreeView32')
                foreach ($t in $w.FindAll($scope, $tree)) { if ((Root-Of $t) -eq $own) { return $true } }
            }
            return $false
        }

        function Get-Tree([int]$root) {
            $all = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, CreationDate)
            $born = @{}
            foreach ($p in $all) { $born[[uint32]$p.ProcessId] = $p.CreationDate }
            $pids = New-Object 'System.Collections.Generic.HashSet[uint32]'
            if (-not $born.ContainsKey([uint32]$root)) { return , $pids }
            [void]$pids.Add([uint32]$root)
            do {
                $added = $false
                foreach ($p in $all) {
                    $parent = [uint32]$p.ParentProcessId
                    # A parent id is never updated when the parent dies, and ids are reused: a child is one only if born after it.
                    if ($pids.Contains($parent) -and $p.CreationDate -ge $born[$parent] -and $pids.Add([uint32]$p.ProcessId)) { $added = $true }
                }
            } while ($added)
            return , $pids
        }

        """;

    /// <summary>Windows: every visible window of the process tree — top-level, or owned and titled (a dialog) — rendered by
    /// <c>PrintWindow</c> and cropped to the frame the screen shows (no shadow), with its UI Automation tree in window
    /// coordinates. One JSON line per window; a window that could not be read gives an <c>{"error"}</c> line.</summary>
    private const string WindowsScript =
        """
        param([int]$RootPid, [string]$OutDir, [int]$WaitSeconds = 20, [int]$MaxNodes = 400)

        """ + WindowsSetup + """
        function Read-Node($el, [int]$depth, [System.Collections.Generic.List[string]]$lines, [double]$ox, [double]$oy, [string]$parentName) {
            if ($lines.Count -ge $MaxNodes) { return }
            $c = $el.Current
            $type = $c.ControlType.ProgrammaticName -replace '^ControlType\.', ''
            if ($type -eq 'Text' -and $c.Name -eq $parentName) { return }
            $parts = @($type)
            if ($c.Name) { $parts += '"' + ($c.Name -replace '[\r\n]+', ' ') + '"' }
            if ($c.AutomationId) { $parts += '#' + $c.AutomationId }
            $p = $null
            if ($el.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$p)) { $parts += 'value="' + ($p.Current.Value -replace '[\r\n]+', ' ') + '"' }
            if ($el.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { $parts += 'toggle=' + $p.Current.ToggleState }
            if (-not $c.IsEnabled) { $parts += 'disabled' }
            if ($c.IsOffscreen) { $parts += 'offscreen' }
            $r = $c.BoundingRectangle
            if (-not $r.IsEmpty) { $parts += ('[{0},{1} {2}x{3}]' -f [int]($r.X - $ox), [int]($r.Y - $oy), [int]$r.Width, [int]$r.Height) }
            $lines.Add(('  ' * $depth) + ($parts -join ' '))
            $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
            $child = $walker.GetFirstChild($el)
            while ($child -ne $null) {
                if ($lines.Count -ge $MaxNodes) { $lines.Add(('  ' * ($depth + 1)) + '... (tree cut at ' + $MaxNodes + ' nodes)'); return }
                # Another top-level window (a dialog it owns, listed under it, listed among the windows or not): never read
                # inside this one.
                $nested = [int64]$child.Current.NativeWindowHandle
                if ($nested -ne 0 -and [InferpalWin]::GetAncestor([IntPtr]$nested, 2).ToInt64() -ne $script:Root) { $child = $walker.GetNextSibling($child); continue }
                # A control that goes away while it is read (an app updating its window) is left out, not the window.
                try { Read-Node $child ($depth + 1) $lines $ox $oy $c.Name } catch { $lines.Add(('  ' * ($depth + 1)) + '(a control that changed while it was read)') }
                $child = $walker.GetNextSibling($child)
            }
        }

        $deadline = (Get-Date).AddSeconds($WaitSeconds)
        $windows = @()
        while ($true) {
            $windows = @([InferpalWin]::Windows((Get-Tree $RootPid)))
            if ($windows.Count -gt 0 -or (Get-Date) -gt $deadline) { break }
            Start-Sleep -Milliseconds 500
        }
        # A window that just appeared may not have painted yet.
        if ($windows.Count -gt 0) { Start-Sleep -Milliseconds 800 }
        $i = 0
        foreach ($h in $windows) {
            $i++
            try {
                $el = [System.Windows.Automation.AutomationElement]::FromHandle($h)
                $script:Root = $h.ToInt64()
                if (Is-FileDialog $el) {
                    [uint32]$owner = 0; [void][InferpalWin]::GetWindowThreadProcessId($h, [ref]$owner)
                    [pscustomobject]@{ pid = $owner; title = $el.Current.Name; fileDialog = $true; tree = @() } | ConvertTo-Json -Compress -Depth 4
                    continue
                }
                $rect = New-Object InferpalWin+RECT
                [void][InferpalWin]::GetWindowRect($h, [ref]$rect)
                # The frame the screen shows, without the shadow GetWindowRect counts (DWMWA_EXTENDED_FRAME_BOUNDS).
                $frame = New-Object InferpalWin+RECT
                if ([InferpalWin]::DwmGetWindowAttribute($h, 9, [ref]$frame, 16) -ne 0) { $frame = $rect }
                $w = $rect.Right - $rect.Left; $hgt = $rect.Bottom - $rect.Top
                $png = $null
                if (-not [InferpalWin]::IsIconic($h) -and $w -gt 0 -and $hgt -gt 0) {
                    $bmp = New-Object System.Drawing.Bitmap $w, $hgt
                    $g = [System.Drawing.Graphics]::FromImage($bmp)
                    $dc = $g.GetHdc()
                    # 2 = PW_RENDERFULLCONTENT: the window's own rendering, even under another window.
                    $ok = [InferpalWin]::PrintWindow($h, $dc, 2)
                    $g.ReleaseHdc($dc); $g.Dispose()
                    if ($ok) {
                        $crop = New-Object System.Drawing.Rectangle ($frame.Left - $rect.Left), ($frame.Top - $rect.Top), ($frame.Right - $frame.Left), ($frame.Bottom - $frame.Top)
                        $shown = $bmp.Clone($crop, $bmp.PixelFormat)
                        $png = Join-Path $OutDir ("window$i.png")
                        $shown.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
                        $shown.Dispose()
                    }
                    $bmp.Dispose()
                }
                $lines = New-Object 'System.Collections.Generic.List[string]'
                try { Read-Node $el 0 $lines $frame.Left $frame.Top '' } catch { $lines.Add('(the controls changed while they were read)') }
                [uint32]$owner = 0; [void][InferpalWin]::GetWindowThreadProcessId($h, [ref]$owner)
                [pscustomobject]@{ pid = $owner; title = $el.Current.Name; png = $png; width = ($frame.Right - $frame.Left); height = ($frame.Bottom - $frame.Top); tree = $lines.ToArray() } |
                    ConvertTo-Json -Compress -Depth 4
            } catch {
                [pscustomobject]@{ error = $_.Exception.Message } | ConvertTo-Json -Compress
            }
        }
        """;

    /// <summary>macOS, in JavaScript for Automation: <c>list &lt;pids&gt;</c> prints one line per window of those processes
    /// (id, pid, width, height, title — no tab or line break in a title), and <c>NO_SCREEN_RECORDING</c> when the system keeps
    /// the screen from this app; <c>tree &lt;pid&gt; &lt;title&gt; &lt;max&gt;</c> prints that window's controls by System
    /// Events, or <c>NO_ACCESSIBILITY</c>, <c>NO_AUTOMATION</c>, <c>TREE_ERROR …</c>.</summary>
    private const string MacScript = """
        ObjC.import('CoreGraphics');
        ObjC.import('Foundation');
        function flat(s) { return String(s).replace(/[\t\r\n]+/g, ' '); }
        function list(pids) {
          const wanted = pids.split(',').map(Number);
          const out = [];
          // CoreGraphics' functions of macOS 10.15 are not in the bridge's tables: bound by hand. Not bound, the permission
          // is judged on the capture itself (an empty file).
          try {
            ObjC.bindFunction('CGPreflightScreenCaptureAccess', ['bool', []]);
            if (!$.CGPreflightScreenCaptureAccess()) out.push('NO_SCREEN_RECORDING');
          } catch (e) {}
          // kCGWindowListOptionOnScreenOnly (1) | kCGWindowListExcludeDesktopElements (16), kCGNullWindowID (0).
          const info = ObjC.castRefToObject($.CGWindowListCopyWindowInfo(1 | 16, 0));
          for (let i = 0; i < info.count; i++) {
            const d = info.objectAtIndex(i);
            const pid = d.objectForKey('kCGWindowOwnerPID').intValue;
            const layer = d.objectForKey('kCGWindowLayer').intValue;
            if (!wanted.includes(pid) || layer !== 0) continue;
            const b = d.objectForKey('kCGWindowBounds');
            const name = d.objectForKey('kCGWindowName');
            out.push([d.objectForKey('kCGWindowNumber').intValue, pid, b.objectForKey('Width').doubleValue,
                      b.objectForKey('Height').doubleValue, name.isNil() ? '' : flat(name.js)].join('\t'));
          }
          return out.join('\n');
        }
        function refused(e) {
          const n = e && e.errorNumber;
          if (n === -1743) return 'NO_AUTOMATION';
          if (n === -1719 || n === -25211) return 'NO_ACCESSIBILITY';
          return 'TREE_ERROR ' + flat(e && e.message ? e.message : e);
        }
        function tree(pid, title, max) {
          const lines = [];
          let proc, windows;
          try {
            const se = Application('System Events');
            const procs = se.processes.whose({ unixId: Number(pid) })();
            if (procs.length === 0) return 'TREE_ERROR System Events does not list the process ' + pid;
            proc = procs[0];
            windows = proc.windows();
          } catch (e) { return refused(e); }
          let win = windows.find(w => { try { return flat(w.name()) === title; } catch (e) { return false; } }) || windows[0];
          if (!win) return 'TREE_ERROR System Events lists no window for this process (an app that does not publish its controls)';
          // An open or save panel, told as the action script tells it — a column browser, or a sheet holding a name field and a
          // pop-up (the panel collapsed) — over the whole window, never the tree's first nodes: its contents are never read.
          try {
            const roles = win.entireContents().map((e) => { try { return e.role(); } catch (x) { return ''; } });
            if (roles.includes('AXBrowser') || (roles.includes('AXSheet') && roles.includes('AXTextField') && roles.includes('AXPopUpButton')))
              return 'FILE_DIALOG';
          } catch (e) {}
          let origin = [0, 0];
          try { origin = win.position(); } catch (e) {}
          function walk(el, depth) {
            if (lines.length >= max) return;
            const parts = [];
            try { parts.push(el.role().replace(/^AX/, '')); } catch (e) { parts.push('?'); }
            try { const n = el.name(); if (n) parts.push('"' + flat(n) + '"'); } catch (e) {}
            try { const d = el.description(); if (d && d !== 'missing value' && parts.indexOf('"' + flat(d) + '"') < 0) parts.push('(' + flat(d) + ')'); } catch (e) {}
            try { const v = el.value(); if (v !== null && v !== undefined && typeof v !== 'object') parts.push('value="' + flat(v) + '"'); } catch (e) {}
            try { if (el.enabled() === false) parts.push('disabled'); } catch (e) {}
            try { const p = el.position(), s = el.size(); parts.push('[' + Math.round(p[0] - origin[0]) + ',' + Math.round(p[1] - origin[1]) + ' ' + Math.round(s[0]) + 'x' + Math.round(s[1]) + ']'); } catch (e) {}
            lines.push('  '.repeat(depth) + parts.join(' '));
            let children = [];
            try { children = el.uiElements(); } catch (e) {}
            for (const c of children) {
              if (lines.length >= max) { lines.push('  '.repeat(depth + 1) + '... (tree cut at ' + max + ' nodes)'); return; }
              walk(c, depth + 1);
            }
          }
          try { walk(win, 0); } catch (e) { if (lines.length === 0) return refused(e); }
          return lines.join('\n');
        }
        function run(argv) {
          if (argv[0] === 'list') return list(argv[1]);
          if (argv[0] === 'tree') return tree(argv[1], argv[2] || '', Number(argv[3] || 400));
          return '';
        }
        """;

    /// <summary>Linux: the windows of the given processes by AT-SPI, one JSON line each (as the Windows script writes them,
    /// no picture), or <c>NO_ATSPI</c>. A node or an app that goes away while it is read is left out, never the rest.</summary>
    private const string LinuxTreeScript = """
        import json, sys
        try:
            import gi
            gi.require_version('Atspi', '2.0')
            from gi.repository import Atspi
            desktop = Atspi.get_desktop(0)
        except Exception:
            print('NO_ATSPI')
            sys.exit(0)
        pids = set(int(p) for p in sys.argv[1].split(','))
        limit = int(sys.argv[2])

        def flat(s):
            return ' '.join(str(s).split())

        def walk(node, depth, lines, origin):
            if len(lines) >= limit:
                return
            try:
                parts = [node.get_role_name().title().replace(' ', '')]
                name = node.get_name()
                if name:
                    parts.append('"' + flat(name) + '"')
                try:
                    value = node.get_value_iface()
                    if value is not None:
                        parts.append('value="%s"' % value.get_current_value())
                except Exception:
                    pass
                try:
                    text = node.get_text_iface()
                    if text is not None and node.get_role_name() in ('text', 'entry', 'password text'):
                        parts.append('value="%s"' % flat(text.get_text(0, text.get_character_count())))
                except Exception:
                    pass
                states = node.get_state_set()
                if not states.contains(Atspi.StateType.SENSITIVE):
                    parts.append('disabled')
                if not states.contains(Atspi.StateType.SHOWING):
                    parts.append('offscreen')
                try:
                    r = node.get_extents(Atspi.CoordType.SCREEN)
                    parts.append('[%d,%d %dx%d]' % (r.x - origin[0], r.y - origin[1], r.width, r.height))
                except Exception:
                    pass
                lines.append('  ' * depth + ' '.join(parts))
                count = node.get_child_count()
            except Exception:
                lines.append('  ' * depth + '(a control that changed while it was read)')
                return
            for i in range(count):
                if len(lines) >= limit:
                    lines.append('  ' * (depth + 1) + '... (tree cut at %d nodes)' % limit)
                    return
                try:
                    child = node.get_child_at_index(i)
                except Exception:
                    continue
                if child is not None:
                    walk(child, depth + 1, lines, origin)

        for i in range(desktop.get_child_count()):
            try:
                app = desktop.get_child_at_index(i)
                if app is None or app.get_process_id() not in pids:
                    continue
                pid = app.get_process_id()
                windows = [app.get_child_at_index(j) for j in range(app.get_child_count())]
            except Exception:
                continue
            for window in windows:
                if window is None:
                    continue
                try:
                    r = window.get_extents(Atspi.CoordType.SCREEN)
                    origin = (r.x, r.y)
                except Exception:
                    origin = (0, 0)
                lines = []
                walk(window, 0, lines, origin)
                try:
                    title = window.get_name() or ''
                except Exception:
                    title = ''
                print(json.dumps({'pid': pid, 'title': title, 'tree': lines}), flush=True)
        """;
}

/// <summary>An X window dump (<c>xwd</c>) to a PNG: the TrueColor formats an X server writes (24 and 32 bits per pixel).</summary>
internal static class Xwd
{
    /// <summary>The largest picture read (16 Mpx): a dump's header is the X server's word, never a size to allocate blindly.</summary>
    private const long MaxPixels = 16L * 1024 * 1024;

    /// <summary>The picture of <paramref name="dump"/>, or <c>(null, 0, 0)</c> for a format this does not read or a dump that
    /// does not hold what its header says.</summary>
    internal static (byte[]? Png, int Width, int Height) ToPng(byte[] dump)
    {
        if (dump.Length < 100) return (null, 0, 0);
        long U(int at) => (long)dump[at] << 24 | (long)dump[at + 1] << 16 | (long)dump[at + 2] << 8 | dump[at + 3];
        long headerSize = U(0), format = U(8), width = U(16), height = U(20), byteOrder = U(28);
        long bitsPerPixel = U(44), bytesPerLine = U(48), colors = U(76);
        uint red = (uint)U(56), green = (uint)U(60), blue = (uint)U(64);
        if (format != 2 || bitsPerPixel is not (24 or 32) || headerSize < 100 || width <= 0 || height <= 0 || width * height > MaxPixels)
            return (null, 0, 0);
        var step = bitsPerPixel / 8;
        var start = headerSize + colors * 12;
        if (bytesPerLine < width * step || start + bytesPerLine * height > dump.Length) return (null, 0, 0);
        var rgb = new byte[width * height * 3];
        for (var y = 0L; y < height; y++)
            for (var x = 0L; x < width; x++)
            {
                var at = start + y * bytesPerLine + x * step;
                uint pixel = 0;
                for (var b = 0; b < step; b++)
                    pixel |= byteOrder == 0 ? (uint)dump[at + b] << (8 * b) : (uint)dump[at + b] << (int)(8 * (step - 1 - b));
                var o = (y * width + x) * 3;
                rgb[o] = Channel(pixel, red);
                rgb[o + 1] = Channel(pixel, green);
                rgb[o + 2] = Channel(pixel, blue);
            }
        using var png = new MemoryStream();
        new StbImageWriteSharp.ImageWriter().WritePng(rgb, (int)width, (int)height, StbImageWriteSharp.ColorComponents.RedGreenBlue, png);
        return (png.ToArray(), (int)width, (int)height);
    }

    private static byte Channel(uint pixel, uint mask)
    {
        if (mask == 0) return 0;
        var shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        var max = mask >> shift;
        return (byte)(((pixel & mask) >> shift) * 255 / max);
    }
}
