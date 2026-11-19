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
        if (PageCapture.FindAll().FirstOrDefault(b => b.Engine == CaptureEngine.Chromium) is not { } browser) return null;
        using var pages = WorkspacePages.Start(_dir.FullName);
        var (png, why) = await PageCapture.CaptureAsync(browser, pages.UrlOf("page.html"), width, CancellationToken.None);
        Assert.True(png is not null, why);
        var image = StbImageSharp.ImageResult.FromMemory(png!, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        Assert.Equal(width, image.Width);
        var at = (image.Height / 2 * image.Width + image.Width / 2) * 4;
        return (image.Data[at], image.Data[at + 1], image.Data[at + 2]);
    }

    [Fact]
    public async Task APhonesWidth_IsTheWidthThePageIsLaidOutAt()
    {
        if (await PixelAtAsync(ViewPageTool.DefaultNarrow) is not { } phone) return;   // undecided: no Chromium browser here
        Assert.Equal(((byte)0xff, (byte)0, (byte)0), phone);
    }

    [Fact]
    public async Task ALaptopsWidth_IsLaidOutWide()
    {
        if (await PixelAtAsync(ViewPageTool.DefaultWide) is not { } laptop) return;   // undecided: no Chromium browser here
        Assert.Equal(((byte)0, (byte)0, (byte)0xff), laptop);                       // reference arm
    }
}
