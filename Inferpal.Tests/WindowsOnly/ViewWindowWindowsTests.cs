using System.IO;
using System.Diagnostics;
using System.Text.Json;
using Inferpal.Services.Documents;
using Inferpal.Services.Shell;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>view_window</c> on Windows, for real (pari 102, gate B): an app the job's shell starts as its CHILD is seen — its green
/// window on the picture, its disabled button in the tree — and a red window of another process, open beside it, is never
/// given, neither picture nor tree.
/// </summary>
public sealed class ViewWindowWindowsTests : IDisposable
{
    private readonly BackgroundShellRegistry _jobs = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("inferpal-viewwindow-");
    private Process? _other;

    public void Dispose()
    {
        foreach (var (id, _, _) in _jobs.List()) _jobs.Stop(id);
        _jobs.Dispose();
        try { _other?.Kill(entireProcessTree: true); } catch { }
        _other?.Dispose();
        try { _dir.Delete(recursive: true); } catch { }
    }

    private string Form(string name, string title, string colour) =>
        Write(name, $$"""
            Add-Type -AssemblyName System.Windows.Forms, System.Drawing
            $f = New-Object System.Windows.Forms.Form
            $f.Text = '{{title}}'; $f.Width = 420; $f.Height = 300; $f.StartPosition = 'Manual'; $f.Left = 40; $f.Top = 40
            $f.BackColor = [System.Drawing.Color]::{{colour}}
            $b = New-Object System.Windows.Forms.Button
            $b.Text = 'Pay now'; $b.Enabled = $false; $b.Left = 20; $b.Top = 20; $b.Width = 120
            $f.Controls.Add($b)
            [System.Windows.Forms.Application]::Run($f)
            """);

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir.FullName, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task TheJobsChildWindow_IsSeen_AndAnotherProcesssWindow_IsNot()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        // Another app, red, not under the job: open first, so it is there when the job's window is looked for.
        _other = Process.Start(new ProcessStartInfo(ps, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Form("other.ps1", "Other app", "Red")])
                               { UseShellExecute = false, CreateNoWindow = true });
        var app = Form("app.ps1", "Order form", "Lime");
        var job = _jobs.Start($"& '{ps}' -NoProfile -ExecutionPolicy Bypass -File '{app}'", "app", _dir.FullName);
        var images = new ToolImageInbox();
        var tool = new ViewWindowTool(_jobs, images);

        var said = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { job, wait = 40 }), CancellationToken.None);

        Assert.Contains("## Window \"Order form\"", said);
        // The role is the toolkit's (WinForms names a disabled button a Pane): the name and the state are what count.
        Assert.True(said.Contains("\"Pay now\""), said);
        Assert.Matches(@"""Pay now"".* disabled", said);
        Assert.DoesNotContain("Other app", said);
        var image = Assert.Single(images.Take());
        var pixels = StbImageSharp.ImageResult.FromMemory(Convert.FromBase64String(image.Base64), StbImageSharp.ColorComponents.RedGreenBlue).Data;
        int lime = 0, red = 0;
        for (var i = 0; i + 2 < pixels.Length; i += 3)
        {
            if (pixels[i] < 40 && pixels[i + 1] > 220 && pixels[i + 2] < 40) lime++;
            if (pixels[i] > 220 && pixels[i + 1] < 40 && pixels[i + 2] < 40) red++;
        }
        Assert.True(lime > pixels.Length / 3 / 4, $"only {lime} lime pixels: the app's window is not on the picture.\n{said}");
        Assert.Equal(0, red);
    }

    /// <summary>"No window" alone does not say whether the job's processes were found, or their windows found and left out
    /// (hidden, cloaked, owned without a title): the look says which, with the processes and windows it counted.</summary>
    [Fact]
    public async Task AJobWithNoWindow_SaysWhatTheLookSaw_ItsProcessesAndTheirWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var job = _jobs.Start("Start-Sleep -Seconds 30", "app", _dir.FullName);

        var said = await new ViewWindowTool(_jobs, new ToolImageInbox())
            .ExecuteAsync(JsonSerializer.SerializeToElement(new { job, wait = 1 }), CancellationToken.None);

        Assert.Contains($"{job} shows no window after 1 s", said);
        Assert.Matches(SawNoWindow, said);
    }

    /// <summary>What a look that found no window says it saw.</summary>
    internal const string SawNoWindow = @"[1-9]\d* process\(es\) in the job's tree, (no top-level window|\d+ top-level window\(s\): "
                                        + @"\d+ shown, \d+ hidden, \d+ cloaked, \d+ owned without a title, \d+ not responding)";
}
