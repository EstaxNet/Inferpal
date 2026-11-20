using System.IO;
using Inferpal.Services.Documents;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A page is laid out at the width it is looked at with: a headless Chromium keeps its window at least about 520 px wide,
/// so from the command line a phone's 390 px is laid out at 496 and the picture cut — a phone that is not one. Driven through
/// its DevTools protocol, the page's viewport is the width asked (<see cref="ChromiumDevTools"/>).
/// </summary>
public sealed class PageCaptureWidthTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("inferpal-capture-width-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch { }
    }

    /// <summary>Red when the page is laid out at most 400 px wide, blue otherwise: one pixel says which width it got.</summary>
    private const string Page =
        "<!doctype html><html><head><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><style>"
        + "body{margin:0;background:#0000ff} @media (max-width:400px){body{background:#ff0000}}</style></head><body></body></html>";

    private async Task<(byte R, byte G, byte B)?> PixelAtAsync(int width)
    {
        File.WriteAllText(Path.Combine(_dir.FullName, "page.html"), Page);
        using var pages = WorkspacePages.Start(_dir.FullName);
        // The first Chromium browser that starts here: Edge on a Linux whose sandbox helper is not set up does not, and the
        // product hands over to the next one (ViewPageToolTests holds that hand-over).
        foreach (var browser in PageCapture.FindAll().Where(b => b.Engine == CaptureEngine.Chromium))
        {
            var (png, _, note) = await PageCapture.CaptureAsync(browser, pages.UrlOf("page.html"), width, CancellationToken.None);
            if (png is null) continue;
            // Captured from the command line, which said so: the protocol's layout is not what this run can judge (a busy
            // machine, a policy), and APhoneCapture_ThatCannotBeLaidOutNarrow_SaysSo holds what is said then.
            if (note is not null) return null;
            var image = StbImageSharp.ImageResult.FromMemory(png, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            Assert.Equal(width, image.Width);
            var at = (image.Height / 2 * image.Width + image.Width / 2) * 4;
            return (image.Data[at], image.Data[at + 1], image.Data[at + 2]);
        }
        return null;
    }

    [Fact]
    public async Task APhonesWidth_IsTheWidthThePageIsLaidOutAt()
    {
        if (await PixelAtAsync(ViewPageTool.DefaultNarrow) is not { } phone) return;   // undecided: no Chromium browser starts here
        Assert.Equal(((byte)0xff, (byte)0, (byte)0), phone);
    }

    [Fact]
    public void ABrowserWhosePortDidNotOpen_IsCapturedFromItsCommandLine_ForAWhile_AndDrivenWhenItOpens()
    {
        // A busy machine is late for a while; a policy that turns the protocol off never opens it. A key of this test's own.
        const string browser = "late-browser-of-this-test";
        var at = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        ChromiumDevTools.PortTimedOut(browser, at);
        Assert.True(ChromiumDevTools.NoProtocol(browser, at.AddMinutes(1)));
        Assert.False(ChromiumDevTools.NoProtocol(browser, at + ChromiumDevTools.NoProtocolFor));   // driven again afterwards
        ChromiumDevTools.PortTimedOut(browser, at);
        ChromiumDevTools.PortOpened(browser);
        Assert.False(ChromiumDevTools.NoProtocol(browser, at.AddMinutes(1)));                       // a port that opens clears it
    }

    [Fact]
    public async Task APhoneCapture_ThatCannotBeLaidOutNarrow_SaysSo()
    {
        File.WriteAllText(Path.Combine(_dir.FullName, "page.html"), Page);
        using var pages = WorkspacePages.Start(_dir.FullName);
        foreach (var found in PageCapture.FindAll().Where(b => b.Engine == CaptureEngine.Chromium))
        {
            // The same browser under a path of its own: the protocol memory the other captures read is not touched.
            var browser = found with { Executable = Path.Combine(Path.GetDirectoryName(found.Executable)!, ".", Path.GetFileName(found.Executable)) };
            ChromiumDevTools.PortTimedOut(browser.Executable);
            try
            {
                var (narrow, _, note) = await PageCapture.CaptureAsync(browser, pages.UrlOf("page.html"), ViewPageTool.DefaultNarrow, CancellationToken.None);
                if (narrow is null) continue;   // this one does not start here: the next is tried
                var image = StbImageSharp.ImageResult.FromMemory(narrow, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                var centre = (image.Height / 2 * image.Width + image.Width / 2) * 4;
                var pixel = (image.Data[centre], image.Data[centre + 1], image.Data[centre + 2]);
                if (OperatingSystem.IsWindows())
                {
                    // What the note says is what the picture shows: the wide layout (blue), cut at the width asked.
                    Assert.Equal(((byte)0, (byte)0, (byte)0xff), pixel);
                    Assert.Contains($"at least about {PageCapture.CommandLineNarrowest} px wide", note);
                }
                else
                {
                    // Linux and macOS lay the page out at the width asked from the command line too: nothing to say.
                    Assert.Equal(((byte)0xff, (byte)0, (byte)0), pixel);
                    Assert.Null(note);
                }
                var (wide, _, wideNote) = await PageCapture.CaptureAsync(browser, pages.UrlOf("page.html"), ViewPageTool.DefaultWide, CancellationToken.None);
                if (wide is not null) Assert.Null(wideNote);   // reference arm: a width the command line lays out as asked
                return;
            }
            finally
            {
                ChromiumDevTools.PortOpened(browser.Executable);
            }
        }
        // Undecided: no Chromium browser starts here.
    }

    [Fact]
    public async Task ALaptopsWidth_IsLaidOutWide()
    {
        if (await PixelAtAsync(ViewPageTool.DefaultWide) is not { } laptop) return;   // undecided: no Chromium browser starts here
        Assert.Equal(((byte)0, (byte)0, (byte)0xff), laptop);                       // reference arm
    }
}
