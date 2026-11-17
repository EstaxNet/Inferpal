using System.IO;
using System.Text.Json;
using Inferpal.Services.Documents;
using Inferpal.Services.Shell;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>view_window</c> looks at the app the agent launched (pari 102): the job's windows, their picture for a model that sees and
/// their tree for every model, cut where the answer would be too long and said; a job with no window says what it printed.
/// </summary>
public sealed class ViewWindowToolTests : IDisposable
{
    private readonly BackgroundShellRegistry _jobs = new();

    public void Dispose()
    {
        foreach (var (id, _, _) in _jobs.List()) _jobs.Stop(id);
        _jobs.Dispose();
    }

    private static Task<string> Look(ViewWindowTool tool, object args) =>
        tool.ExecuteAsync(JsonSerializer.SerializeToElement(args), CancellationToken.None);

    private string LongJob() => _jobs.Start(OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 25" : "sleep 25", "app", Path.GetTempPath());

    [Fact]
    public void TheTool_IsOffered_OnlyWhileAJobRuns()
    {
        var tool = new ViewWindowTool(_jobs, new ToolImageInbox());
        Assert.False(tool.IsOffered);

        var id = LongJob();
        Assert.True(tool.IsOffered);

        _jobs.Stop(id);
        Assert.False(tool.IsOffered);
    }

    [Fact]
    public async Task AnUnknownJob_IsNamed_WithTheJobsThatRun()
    {
        var running = LongJob();
        var tool = new ViewWindowTool(_jobs, new ToolImageInbox(), (_, _, _) => throw new InvalidOperationException("never looked at"));

        var said = await Look(tool, new { job = "bg99" });

        Assert.Contains("No running background job 'bg99'", said);
        Assert.Contains(running, said);
    }

    [Fact]
    public async Task AJobWithNoWindow_SaysSo_AndWhatItPrinted()
    {
        var id = _jobs.Start(OperatingSystem.IsWindows() ? "Write-Output 'listening on 5000'; Start-Sleep -Seconds 25"
                                                         : "echo 'listening on 5000'; sleep 25", "app", Path.GetTempPath());
        await Task.Delay(1500);
        var tool = new ViewWindowTool(_jobs, new ToolImageInbox(), (_, _, _) => Task.FromResult(new WindowReading([], null, [])));

        var said = await Look(tool, new { job = id, wait = 1 });

        Assert.Contains($"{id} shows no window after 1 s", said);
        Assert.Contains($"action poll, id {id}", said);
        // The job's output is left for the model's own poll, never taken by the look.
        Assert.Contains("listening on 5000", _jobs.Poll(id).NewOutput);
    }

    [Fact]
    public async Task AShellThatEnded_SaysTheAppWasDetached_AndHowToKeepItAChild()
    {
        var id = _jobs.Start(OperatingSystem.IsWindows() ? "exit 0" : "true", "app", Path.GetTempPath());
        for (var i = 0; i < 100 && _jobs.ProcessIdOf(id) is not null; i++) await Task.Delay(100);
        var tool = new ViewWindowTool(_jobs, new ToolImageInbox(), (_, _, _) => throw new InvalidOperationException("never looked at"));

        var said = await Look(tool, new { job = id });

        Assert.Contains($"The shell of {id} has ended", said);
        Assert.Contains("& .\\App.exe | Out-Null", said);
    }

    [Fact]
    public void AnXWindowDump_ThatDoesNotHoldWhatItsHeaderSays_GivesNoPicture()
    {
        var header = new byte[104];
        void Put(int at, uint v) { header[at] = (byte)(v >> 24); header[at + 1] = (byte)(v >> 16); header[at + 2] = (byte)(v >> 8); header[at + 3] = (byte)v; }
        // 1000 pixels a line, 0 bytes a line: the check on the line's size is what keeps the read inside the dump.
        Put(0, 100); Put(8, 2); Put(16, 1000); Put(20, 1); Put(44, 32); Put(48, 0);
        Assert.Null(Xwd.ToPng(header).Png);
        // 16384 × 16384 claimed: never allocated.
        Put(16, 16384); Put(20, 16384); Put(48, 16384 * 4);
        Assert.Null(Xwd.ToPng(header).Png);
    }

    [Fact]
    public async Task TheTree_GoesToTheModel_AndThePicture_FollowsAsAnImage()
    {
        var id = LongJob();
        var images = new ToolImageInbox();
        var png = TinyPng();
        var tool = new ViewWindowTool(_jobs, images, (_, _, _) => Task.FromResult(new WindowReading(
            [new CapturedWindow("Order", 42, png, 2, 2, ["Window \"Order\" [0,0 2x2]", "  Button \"Order\" disabled [0,0 1x1]"])], null, [])));

        var said = await Look(tool, new { job = id });

        Assert.Contains("## Window \"Order\" — process 42, 2×2: its picture follows this result", said);
        Assert.Contains("  Button \"Order\" disabled [0,0 1x1]", said);
        Assert.Single(images.Take());
    }

    [Fact]
    public async Task ATreeTooLong_IsCut_AndTheCutIsSaid()
    {
        var id = LongJob();
        var lines = Enumerable.Range(0, 2000).Select(i => $"  Text \"line {i:D4} of a very long list\" [0,{i} 10x10]").ToList();
        var tool = new ViewWindowTool(_jobs, new ToolImageInbox(), (_, _, _) => Task.FromResult(new WindowReading(
            [new CapturedWindow("Log", 1, null, 0, 0, lines)], null, [])));

        var said = await Look(tool, new { job = id });

        Assert.Contains("no picture", said);
        Assert.Contains("(tree cut:", said);
        Assert.True(said.Length < ViewWindowTool.MaxTreeChars + 1000, $"{said.Length} characters");
    }

    [Fact]
    public async Task ASystemsRefusal_AndItsNotes_AreSaid()
    {
        var id = LongJob();
        var refused = new ViewWindowTool(_jobs, new ToolImageInbox(),
                                         (_, _, _) => Task.FromResult(new WindowReading([], "there is no display here", [])));
        var noted = new ViewWindowTool(_jobs, new ToolImageInbox(), (_, _, _) => Task.FromResult(new WindowReading(
            [new CapturedWindow("App", 1, null, 0, 0, [])], null, ["macOS has not let the app that runs Inferpal record the screen"])));

        Assert.Contains("could not be looked at: there is no display here", await Look(refused, new { job = id }));
        Assert.Contains("record the screen", await Look(noted, new { job = id }));
    }

    [Fact]
    public void AnXWindowDump_DecodesToItsColours()
    {
        // A 2×1 ZPixmap at 32 bits per pixel, least significant byte first: a red pixel, then a blue one.
        var header = new byte[100];
        void Put(int at, uint v) { header[at] = (byte)(v >> 24); header[at + 1] = (byte)(v >> 16); header[at + 2] = (byte)(v >> 8); header[at + 3] = (byte)v; }
        Put(0, 100); Put(4, 7); Put(8, 2); Put(12, 24); Put(16, 2); Put(20, 1); Put(28, 0); Put(44, 32); Put(48, 8);
        Put(56, 0xFF0000); Put(60, 0x00FF00); Put(64, 0x0000FF); Put(76, 0);
        byte[] pixels = [0x00, 0x00, 0xFF, 0x00, 0xFF, 0x00, 0x00, 0x00];

        var (png, width, height) = Xwd.ToPng([.. header, .. pixels]);

        Assert.Equal((2, 1), (width, height));
        var image = StbImageSharp.ImageResult.FromMemory(png!, StbImageSharp.ColorComponents.RedGreenBlue);
        Assert.Equal(new byte[] { 255, 0, 0, 0, 0, 255 }, image.Data);
    }

    private static byte[] TinyPng()
    {
        using var stream = new MemoryStream();
        new StbImageWriteSharp.ImageWriter().WritePng(new byte[2 * 2 * 3], 2, 2, StbImageWriteSharp.ColorComponents.RedGreenBlue, stream);
        return stream.ToArray();
    }
}
