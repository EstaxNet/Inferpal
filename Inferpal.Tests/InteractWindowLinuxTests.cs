using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Documents;
using Inferpal.Services.Shell;
using Inferpal.Services.Tools;
using Xunit;
using Xunit.Abstractions;

namespace Inferpal.Tests;

/// <summary>
/// <c>interact_window</c> on Linux, for real (pari 126): in a GTK app the job started, a name typed, a size chosen in a combo
/// box, a box ticked and a button clicked — through AT-SPI — change what its window shows, and its picture is saved; a file
/// chooser the app opens is refused and seen by its title alone. Undecided (returns) without a display, AT-SPI and GTK for
/// python3 — the CI's Linux runner has none; measured under WSLg.
/// </summary>
public sealed class InteractWindowLinuxTests(ITestOutputHelper output) : IDisposable
{
    private readonly BackgroundShellRegistry _jobs = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("inferpal-interactwindow-");

    public void Dispose()
    {
        foreach (var (id, _, _) in _jobs.List()) _jobs.Stop(id);
        _jobs.Dispose();
        try { _dir.Delete(recursive: true); } catch { }
    }

    private const string GtkApp = """
        import gi
        gi.require_version('Gtk', '3.0')
        from gi.repository import Gtk
        w = Gtk.Window(title='Order form')
        w.set_default_size(420, 280)
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
        name = Gtk.Entry()
        name.get_accessible().set_name('Customer name')
        size = Gtk.ComboBoxText()
        for s in ('Small', 'Medium', 'Large'):
            size.append_text(s)
        size.get_accessible().set_name('Size')
        gift = Gtk.CheckButton(label='Gift wrap')
        status = Gtk.Label(label='Nothing ordered')
        order = Gtk.Button(label='Order')
        order.connect('clicked', lambda b: status.set_text('Ordered for %s, %s, gift %s' % (name.get_text(), size.get_active_text(), gift.get_active())))
        opener = Gtk.Button(label='Open file')
        def open_file(_):
            d = Gtk.FileChooserDialog(title='Pick a file', parent=w, action=Gtk.FileChooserAction.OPEN)
            d.add_buttons('Cancel', Gtk.ResponseType.CANCEL)
            d.show()
        opener.connect('clicked', open_file)
        for c in (name, size, gift, order, opener, status):
            box.pack_start(c, False, False, 0)
        w.add(box)
        w.connect('destroy', Gtk.main_quit)
        w.show_all()
        Gtk.main()
        """;

    /// <summary>Whether python3 reaches AT-SPI and GTK here.</summary>
    private static bool PythonHasAtspiAndGtk()
    {
        if (ShellLauncher.FindOnPath("python3") is not { } python) return false;
        var psi = new ProcessStartInfo(python, ["-c", "import gi; gi.require_version('Atspi','2.0'); gi.require_version('Gtk','3.0'); from gi.repository import Atspi, Gtk"])
        { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        return p.WaitForExit(30_000) && p.ExitCode == 0;
    }

    private sealed class Yes : IApprovalService
    {
        public int Asked;
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               DiffInfo? diff = null, bool forcePrompt = false)
        {
            Asked++;
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task OnLinux_TheStepsChangeTheJobsWindow_AndAFileChooserIsRefused()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) || !PythonHasAtspiAndGtk())
            return;
        var app = Path.Combine(_dir.FullName, "app.py");
        File.WriteAllText(app, GtkApp);
        // On X11 (XWayland under a Wayland desktop): xwd takes the picture of an X window only.
        var job = _jobs.Start($"GDK_BACKEND=x11 python3 '{app}'", "app", _dir.FullName);
        var history = new FileHistoryService();
        history.BeginRun();
        var approval = new Yes();
        var tool = new InteractWindowTool(_jobs, new ToolImageInbox(), approval, () => _dir.FullName, history);

        var said = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new
        {
            job, wait = 30, save_to = "docs/order.png",
            steps = new object[]
            {
                new { action = "type", control = "Customer name", text = "Ada" },
                new { action = "select", control = "Size", item = "Medium" },
                new { action = "toggle", control = "Gift wrap" },
                new { action = "click", control = "Order" },
                new { action = "wait_for", control = "Ordered for Ada", timeout_ms = 5000 },
            },
        }), CancellationToken.None);
        output.WriteLine(said);

        Assert.True(said.Contains("Ordered for Ada, Medium, gift True"), said);
        Assert.Contains("5. wait_for Ordered for Ada: done", said);
        if (said.Contains("does not list its windows (_NET_CLIENT_LIST)"))
        {
            // A window manager that lists no windows (WSLg's): the tree only, and the capture not saved — both said.
            Assert.Contains(CaptureSaving.NotSavedPrefix + "docs/order.png: no window of the app gave a picture", said);
            Assert.Equal(0, approval.Asked);
        }
        else
        {
            Assert.True(said.Contains("is saved to docs/order.png"), said);
            Assert.Equal(1, approval.Asked);
            Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, File.ReadAllBytes(Path.Combine(_dir.FullName, "docs", "order.png"))[..4]);
        }

        var dialog = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new
        {
            job,
            steps = new object[]
            {
                new { action = "click", control = "Open file" },
                new { action = "wait_for", control = "Pick a file", timeout_ms = 8000 },
                new { action = "click", control = "Order" },
            },
        }), CancellationToken.None);
        output.WriteLine(dialog);

        Assert.True(dialog.Contains("2. wait_for Pick a file: done — Pick a file is there: "), dialog);
        Assert.Contains("3. click Order: NOT DONE — a file dialog is open in the app (\"Pick a file\"): no step acts while it is", dialog);
        Assert.Matches(@"## Window ""Pick a file"" — process \d+: a file dialog\.", dialog);
        Assert.DoesNotContain("File Chooser Widget", dialog);   // the chooser inside the window: its contents

        // Its contents are never searched: a name it may hold is not told apart from one it does not.
        var probe = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new
        {
            job, steps = new object[] { new { action = "wait_for", control = "Cancel", timeout_ms = 500 } },
        }), CancellationToken.None);
        Assert.True(probe.Contains("1. wait_for Cancel: NOT DONE — Cancel did not appear (a file dialog is open, whose contents are never searched)"), probe);
    }
}
