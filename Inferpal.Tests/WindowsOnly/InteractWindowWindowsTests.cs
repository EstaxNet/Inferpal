using System.IO;
using System.Diagnostics;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Documents;
using Inferpal.Services.Execution;
using Inferpal.Services.Shell;
using Inferpal.Services.Tools;
using Xunit;
using Xunit.Abstractions;

namespace Inferpal.Tests;

/// <summary>
/// <c>interact_window</c> on Windows, for real (pari 126): in the app the job started, a name typed, a size chosen in a
/// drop-down, a box ticked and a button clicked — through UI Automation — change what its window shows, and its picture is
/// saved into the project; another process's window, with a button of the same name, is never acted in; a file dialog the
/// app opens is refused.
/// </summary>
public sealed class InteractWindowWindowsTests(ITestOutputHelper output) : IDisposable
{
    private readonly BackgroundShellRegistry _jobs = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("inferpal-interactwindow-");
    private Process? _other;

    public void Dispose()
    {
        foreach (var (id, _, _) in _jobs.List()) _jobs.Stop(id);
        _jobs.Dispose();
        try { _other?.Kill(entireProcessTree: true); } catch { }
        _other?.Dispose();
        try { _dir.Delete(recursive: true); } catch { }
    }

    /// <summary>The real approval path, every card answered "once".</summary>
    private sealed class Yes() : ApprovalServiceBase(InferpalConfig.LoadOwnCopy(), () => Path.GetTempPath())
    {
        public readonly List<ApprovalPrompt> Shown = [];
        protected override Task<ApprovalDecision> PromptUserAsync(ApprovalPrompt prompt, CancellationToken ct)
        {
            Shown.Add(prompt);
            return Task.FromResult(ApprovalDecision.Once);
        }
        protected override Task<ApprovalDecision> PromptUserAsync(string message, DiffInfo? diff, CancellationToken ct) =>
            throw new InvalidOperationException("the card path asks");
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir.FullName, name);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>A WPF window: <c>x:Name</c> is the AutomationId UI Automation gives. ⚠ Not WinForms under Windows PowerShell:
    /// its controls are bare panes named by their handle, with no pattern to act through.</summary>
    private string Wpf(string name, string title, int left, string controls, string script) => Write(name, $$"""
        Add-Type -AssemblyName PresentationFramework
        [xml]$x = @'
        <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                Title="{{title}}" Width="440" Height="320" Left="{{left}}" Top="40" WindowStartupLocation="Manual">
          <StackPanel Margin="12">{{controls}}</StackPanel>
        </Window>
        '@
        $w = [Windows.Markup.XamlReader]::Load((New-Object System.Xml.XmlNodeReader $x))
        {{script}}
        [void]$w.ShowDialog()
        """);

    /// <summary>An order form: its Order button writes what was filled in on its status line.</summary>
    private string OrderForm() => Wpf("app.ps1", "Order form", 40, """
        <TextBox x:Name="nameBox" Width="200" HorizontalAlignment="Left"/>
        <ComboBox x:Name="sizeList" Width="120" HorizontalAlignment="Left">
          <ComboBoxItem>Small</ComboBoxItem><ComboBoxItem>Medium</ComboBoxItem><ComboBoxItem>Large</ComboBoxItem>
        </ComboBox>
        <CheckBox x:Name="giftBox" Content="Gift wrap"/>
        <Button x:Name="orderButton" Content="Order" Width="90" HorizontalAlignment="Left"/>
        <Button x:Name="openButton" Content="Open file" Width="90" HorizontalAlignment="Left"/>
        <TextBlock x:Name="status" Text="Nothing ordered"/>
        """, """
        $name = $w.FindName('nameBox'); $size = $w.FindName('sizeList'); $gift = $w.FindName('giftBox'); $status = $w.FindName('status')
        $w.FindName('orderButton').Add_Click({ $status.Text = "Ordered for $($name.Text), $($size.SelectedItem.Content), gift $($gift.IsChecked)" })
        # Opened after the click returns: the click's own call never waits on a modal dialog.
        $w.FindName('openButton').Add_Click({ [void]$w.Dispatcher.BeginInvoke([Action]{ $d = New-Object Microsoft.Win32.OpenFileDialog; $d.Title = 'Pick a file'; [void]$d.ShowDialog($w) }) })
        """);

    /// <summary>Another app, not under the job: its own Order button would write <paramref name="marker"/>.</summary>
    private string OtherForm(string marker) => Wpf("other.ps1", "Other app", 520,
        """<Button x:Name="orderButton" Content="Order" Width="90" HorizontalAlignment="Left"/>""",
        $"$w.FindName('orderButton').Add_Click({{ Set-Content -Path '{marker}' -Value 'clicked' }})");

    private static Task<string> Call(InteractWindowTool tool, object args) =>
        tool.ExecuteAsync(JsonSerializer.SerializeToElement(args), CancellationToken.None);

    [Fact]
    public async Task TheStepsChangeTheJobsWindow_TheOtherAppsIsNeverTouched_AndThePictureIsSaved()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var marker = Path.Combine(_dir.FullName, "other-clicked.txt");
        _other = Process.Start(new ProcessStartInfo(ps, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", OtherForm(marker)])
                               { UseShellExecute = false, CreateNoWindow = true });
        var job = _jobs.Start($"& '{ps}' -NoProfile -ExecutionPolicy Bypass -File '{OrderForm()}'", "app", _dir.FullName);
        var approval = new Yes();
        var history = new FileHistoryService();
        history.BeginRun();
        var images = new ToolImageInbox();
        var tool = new InteractWindowTool(_jobs, images, approval, () => _dir.FullName, history);

        var said = await Call(tool, new
        {
            job, wait = 40, save_to = "docs/order.png",
            steps = new object[]
            {
                new { action = "type", control = "#nameBox", text = "Ada" },
                new { action = "select", control = "sizeList", item = "Medium" },
                new { action = "toggle", control = "Gift wrap" },
                new { action = "click", control = "\"Order\"" },
                new { action = "wait_for", control = "Ordered for Ada", timeout_ms = 5000 },
            },
        });
        output.WriteLine(said);   // the whole report when an assertion below fails: it only quotes its first characters

        Assert.Contains("1. type nameBox: done", said);
        Assert.Contains("4. click Order: done", said);
        Assert.Contains("Ordered for Ada, Medium, gift True", said);   // the tree after the steps
        Assert.DoesNotContain("Other app", said);
        Assert.False(File.Exists(marker), "the other app's Order button was clicked");
        Assert.Contains("is saved to docs/order.png", said);
        var saved = File.ReadAllBytes(Path.Combine(_dir.FullName, "docs", "order.png"));
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, saved[..4]);
        var card = Assert.Single(approval.Shown);
        Assert.Equal("write_file", card.ToolName);
        Assert.True(card.Diff is { Creates: true, Binary.After: not null });
        Assert.NotEmpty(images.Take());

        // A file dialog the app opens: waited for by its title, and while it is open no step acts.
        var dialog = await Call(tool, new
        {
            job,
            steps = new object[]
            {
                new { action = "click", control = "openButton" },
                new { action = "wait_for", control = "Pick a file", timeout_ms = 8000 },
                new { action = "click", control = "orderButton" },
            },
        });
        output.WriteLine(dialog);
        Assert.True(dialog.Contains("2. wait_for Pick a file: done — Pick a file is there: Window \"Pick a file\", a file dialog"), dialog);
        Assert.Contains("3. click orderButton: NOT DONE — a file dialog is open in the app (\"Pick a file\"): no step acts while it is", dialog);
        // Seen by its title alone: neither its picture nor its tree, which list the person's folders — and the window that
        // owns it (UI Automation lists the dialog under it) is still the app's own, its picture and tree given without it.
        Assert.Matches(@"## Window ""Pick a file"" — process \d+: a file dialog\.", dialog);
        Assert.Matches(@"## Window ""Order form"" — process \d+, \d+×\d+: its picture follows", dialog);
        Assert.DoesNotContain("System.ItemNameDisplay", dialog);   // the column of the names in a folder's view

        // Its contents are never searched: a name it may hold is not told apart from one it does not.
        var probe = await Call(tool, new { job, steps = new object[] { new { action = "wait_for", control = "#2", timeout_ms = 500 } } });
        Assert.True(probe.Contains("1. wait_for 2: NOT DONE — 2 did not appear (a file dialog is open, whose contents are never searched)"), probe);
    }

    /// <summary>Steps for an app that shows no window are refused, and the refusal says what the look saw: the job's
    /// processes, and their windows left out.</summary>
    [Fact]
    public async Task StepsForAJobWithNoWindow_AreRefused_WithWhatTheLookSaw()
    {
        if (!OperatingSystem.IsWindows()) return;
        var job = _jobs.Start("Start-Sleep -Seconds 30", "app", _dir.FullName);
        var tool = new InteractWindowTool(_jobs, new ToolImageInbox(), new Yes(), () => _dir.FullName, new FileHistoryService());

        var said = await Call(tool, new { job, wait = 1, steps = new object[] { new { action = "click", control = "Order" } } });
        output.WriteLine(said);

        Assert.Contains("the app shows no window after 1 s", said);
        Assert.Matches(ViewWindowWindowsTests.SawNoWindow, said);
    }
}
