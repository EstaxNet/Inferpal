using System.IO;
using System.Text.Json;
using Inferpal.Services.Editor;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

public class OpenDocumentOverlayTests
{
    [Fact]
    public void SetThenTryGet_ReturnsBufferedText()
    {
        var overlay = new OpenDocumentOverlay();
        overlay.Set(@"C:\proj\a.cs", "content");

        Assert.True(overlay.TryGet(@"C:\proj\a.cs", out var text));
        Assert.Equal("content", text);
    }

    [Fact]
    public void TryGet_IsPathNormalized_AndFollowsThePlatformsCaseFolding()
    {
        var overlay = new OpenDocumentOverlay();
        overlay.Set(TestPaths.P(@"C:\proj\sub\..\A.CS"), "content");

        // `sub\..` collapses on every OS.
        Assert.True(overlay.TryGet(TestPaths.P(@"C:\proj\A.CS"), out var text));
        Assert.Equal("content", text);

        // Case-folding follows the file system: on Linux a.cs and A.CS are two distinct
        // files, and the overlay must NOT hand one buffer out for the other (§23).
        Assert.Equal(!OperatingSystem.IsLinux(),
                     overlay.TryGet(TestPaths.P(@"C:\proj\a.cs"), out _));
    }

    [Fact]
    public void Remove_DropsTheDocument()
    {
        var overlay = new OpenDocumentOverlay();
        overlay.Set(TestPaths.P(@"C:\proj\a.cs"), "content");
        overlay.Remove(TestPaths.P(@"C:\proj\a.cs"));

        Assert.False(overlay.TryGet(TestPaths.P(@"C:\proj\a.cs"), out _));
        Assert.Equal(0, overlay.Count);
    }

    [Fact]
    public void Set_OverwritesPreviousBuffer()
    {
        var overlay = new OpenDocumentOverlay();
        overlay.Set(@"C:\proj\a.cs", "v1");
        overlay.Set(@"C:\proj\a.cs", "v2");

        Assert.True(overlay.TryGet(@"C:\proj\a.cs", out var text));
        Assert.Equal("v2", text);
        Assert.Single(overlay.Paths);
    }

    [Fact]
    public async Task ReadFileTool_PrefersOverlayOverDisk_ThenFallsBackAfterClose()
    {
        var dir = Directory.CreateTempSubdirectory("inferpal-overlay-");
        try
        {
            var file = Path.Combine(dir.FullName, "a.txt");
            await File.WriteAllTextAsync(file, "disk");

            var overlay = new OpenDocumentOverlay();
            overlay.Set(file, "buffer");

            var tool = new ReadFileTool(() => dir.FullName, overlay);
            var args = JsonDocument.Parse(JsonSerializer.Serialize(new { path = file })).RootElement;

            Assert.Equal("buffer", await tool.ExecuteAsync(args, CancellationToken.None));

            overlay.Remove(file);
            Assert.Equal("disk", await tool.ExecuteAsync(args, CancellationToken.None));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ReadFileTool_ServesOverlayForNotYetSavedFile()
    {
        var dir = Directory.CreateTempSubdirectory("inferpal-overlay-");
        try
        {
            // Open-but-never-saved document: exists only in the editor buffer.
            var file = Path.Combine(dir.FullName, "new.txt");
            var overlay = new OpenDocumentOverlay();
            overlay.Set(file, "unsaved");

            var tool = new ReadFileTool(() => dir.FullName, overlay);
            var args = JsonDocument.Parse(JsonSerializer.Serialize(new { path = file })).RootElement;

            Assert.Equal("unsaved", await tool.ExecuteAsync(args, CancellationToken.None));
        }
        finally { dir.Delete(recursive: true); }
    }
}
