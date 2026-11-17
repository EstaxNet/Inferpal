using System.IO;
using System.Text.Json;
using Inferpal.Services.Documents;
using Inferpal.Services.Shell;
using Inferpal.Services.Tools;
using Xunit;
using Xunit.Abstractions;

namespace Inferpal.Tests;

/// <summary>
/// <c>view_window</c> on macOS and Linux, for real (pari 102, gate B). macOS: an AppKit window the job's shell starts as its
/// child is found; its picture and its tree are given — or, when the system keeps them from the app that runs the tests (Screen
/// Recording, Accessibility), the answer says so with the gesture, never a blank picture. Linux with no display: said.
/// </summary>
public sealed class ViewWindowPosixTests(ITestOutputHelper output) : IDisposable
{
    private readonly BackgroundShellRegistry _jobs = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("inferpal-viewwindow-");

    public void Dispose()
    {
        foreach (var (id, _, _) in _jobs.List()) _jobs.Stop(id);
        _jobs.Dispose();
        try { _dir.Delete(recursive: true); } catch { }
    }

    private const string AppKitWindow = """
        ObjC.import('Cocoa');
        function run(argv) {
          const app = $.NSApplication.sharedApplication;
          app.setActivationPolicy($.NSApplicationActivationPolicyRegular);
          const win = $.NSWindow.alloc.initWithContentRectStyleMaskBackingDefer($.NSMakeRect(60, 60, 420, 300),
            $.NSWindowStyleMaskTitled, $.NSBackingStoreBuffered, false);
          win.title = 'Order form';
          win.backgroundColor = $.NSColor.greenColor;
          const button = $.NSButton.alloc.initWithFrame($.NSMakeRect(20, 220, 140, 32));
          button.title = 'Pay now';
          button.enabled = false;
          win.contentView.addSubview(button);
          win.makeKeyAndOrderFront(null);
          app.activateIgnoringOtherApps(true);
          $.NSRunLoop.currentRunLoop.runUntilDate($.NSDate.dateWithTimeIntervalSinceNow(25));
        }
        """;

    [Fact]
    public async Task OnMacOS_TheJobsWindow_IsFound_AndWhatTheSystemKeepsIsSaid()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var script = Path.Combine(_dir.FullName, "window.js");
        File.WriteAllText(script, AppKitWindow);
        var job = _jobs.Start($"/usr/bin/osascript -l JavaScript '{script}'", "app", _dir.FullName);
        var images = new ToolImageInbox();

        var said = await new ViewWindowTool(_jobs, images).ExecuteAsync(JsonSerializer.SerializeToElement(new { job, wait = 15 }), CancellationToken.None);
        output.WriteLine(said);

        Assert.True(said.Contains("## Window"), said);
        var picture = images.Take();
        // The picture, or the sentence that says why there is none — never a picture of nothing.
        Assert.True(picture.Count == 1 || said.Contains("record the screen"), said);
        if (picture.Count == 1)
        {
            var pixels = StbImageSharp.ImageResult.FromMemory(Convert.FromBase64String(picture[0].Base64), StbImageSharp.ColorComponents.RedGreenBlue).Data;
            var green = 0;
            for (var i = 0; i + 2 < pixels.Length; i += 3)
                if (pixels[i] < 60 && pixels[i + 1] > 180 && pixels[i + 2] < 60) green++;
            Assert.True(green > pixels.Length / 3 / 4, $"only {green} green pixels: the window is not on the picture.\n{said}");
        }
        // The tree, or the sentence that says the system keeps it.
        Assert.True(said.Contains("Pay now") || said.Contains("Accessibility"), said);
        if (said.Contains("Pay now")) Assert.Matches(@"""Pay now"".* disabled", said);
    }

    [Fact]
    public async Task OnLinux_WithNoDisplay_TheAnswerSaysSo()
    {
        if (!OperatingSystem.IsLinux() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))) return;
        var job = _jobs.Start("sleep 25", "app", _dir.FullName);

        var said = await new ViewWindowTool(_jobs, new ToolImageInbox()).ExecuteAsync(
            JsonSerializer.SerializeToElement(new { job, wait = 1 }), CancellationToken.None);
        output.WriteLine(said);

        Assert.Contains("there is no display here", said);
    }
}
