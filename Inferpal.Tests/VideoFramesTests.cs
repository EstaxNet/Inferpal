using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Documents;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A screen recording of a bug reads as one contact sheet of frames at even times, each with its time written on it
/// (pari 142): decoded by the system — Windows' media stack, ffmpeg elsewhere — and a GIF that moves by Inferpal itself.
/// A missing decoder is said with what installs it.
/// </summary>
[Collection(CultureSerialCollection.Name)]
public class VideoFramesTests
{
    private static string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "Inferpal.Tests", "Fixtures", "video", name);
    }

    [Fact]
    public async Task AnMp4_ReadsAsItsContactSheet_OrSaysWhatInstallsTheDecoder()
    {
        Strings.ApplyLanguage("en");
        try
        {
            var read = await ChatImages.ReadAsync(File.ReadAllBytes(Fixture("sample.mp4")), "bug.mp4", CancellationToken.None);
            if (read.Image is { } sheet)
            {
                // INFERPAL_DUMP_SHEET=<file>: the sheet written out, to look at it.
                if (Environment.GetEnvironmentVariable("INFERPAL_DUMP_SHEET") is { Length: > 0 } dump)
                    File.WriteAllBytes(dump, Convert.FromBase64String(sheet.Base64));
                Assert.Equal("bug.mp4", sheet.Name);
                Assert.Equal("image/jpeg", sheet.MediaType);
                Assert.Equal("video · 0:06 · 8 pictures", sheet.Video);
                Assert.Equal(4 * 476 + 3 * 4, sheet.Width);                      // four frames a row, never scaled down after
                Assert.Null(read.Note);
                Assert.Equal(sheet.Video, ImageCards.Chip(sheet, pasted: false).Detail);
            }
            else
            {
                // Windows always decodes an H.264 .mp4; elsewhere only ffmpeg does, and its absence is said with its install.
                Assert.False(OperatingSystem.IsWindows(), read.Refusal);
                Assert.Contains("ffmpeg", read.Refusal);
                Assert.Contains("install", read.Refusal);
            }
        }
        finally { Strings.ApplyLanguage(null); }
    }

    [Fact]
    public async Task AGifThatMoves_IsReadByInferpalItself_AsItsSheet()
    {
        Strings.ApplyLanguage("en");
        try
        {
            var read = await ChatImages.ReadAsync(File.ReadAllBytes(Fixture("moving.gif")), "moving.gif", CancellationToken.None);
            var sheet = Assert.IsType<ChatImage>(read.Image);
            Assert.Equal("video · 0:03 · 8 pictures", sheet.Video);
        }
        finally { Strings.ApplyLanguage(null); }
    }

    [Fact]
    public async Task AStillGif_StaysAnImage()
    {
        // REFERENCE ARM: a GIF of one frame is a picture, not a recording.
        var bytes = await File.ReadAllBytesAsync(Fixture("still.gif"));
        Assert.False(VideoFrames.Layout(bytes)!.Moves);
        var read = await ChatImages.ReadAsync(bytes, "still.gif", CancellationToken.None);
        var image = Assert.IsType<ChatImage>(read.Image);
        Assert.Null(image.Video);
        Assert.Equal((64, 36), (image.Width, image.Height));
    }

    [Fact]
    public void AGifsFrames_AreCountedByItsBlocks_NeverByBytesThatLookLikeOne()
    {
        // A palette entry and image data holding 0x21 0xF9, the bytes of a frame's control block: still one frame.
        var gif = new List<byte>("GIF89a"u8.ToArray()) { 1, 0, 1, 0, 0x80, 0, 0 };   // 1 × 1, a 2-colour palette
        gif.AddRange([0x21, 0xF9, 0x21, 0xF9, 0x21, 0xF9]);
        gif.AddRange([0x21, 0xF9, 4, 0, 10, 0, 0, 0]);                                     // its control block, 0.1 s
        gif.AddRange([0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 3, 0x21, 0xF9, 0x44, 0]);       // its image
        gif.Add(0x3B);
        var layout = VideoFrames.Layout([.. gif]);
        Assert.NotNull(layout);
        Assert.Single(layout.Delays);
        Assert.False(layout.Moves);
        Assert.Equal(12, VideoFrames.Layout(File.ReadAllBytes(Fixture("moving.gif")))!.Delays.Count);   // 3 s at 4 frames a second
    }

    [Fact]
    public void AGifCanvasLargerThanAnImage_IsNotDecoded()
    {
        var bytes = File.ReadAllBytes(Fixture("moving.gif"));
        bytes[6] = 0xFF; bytes[7] = 0xFF; bytes[8] = 0xFF; bytes[9] = 0xFF;                 // 65535 × 65535 declared
        Assert.Null(VideoFrames.FromGif(bytes).Frames);
    }

    [Fact]
    public void AFrameThatDoesNotDecode_LeavesEveryOtherFrameItsOwnTime()
    {
        // Frames at 0:05, 0:15 (damaged), 0:25: the second cell must read 0:25, never 0:15.
        var frames = new[]
        {
            new VideoFrames.Frame(TimeSpan.FromSeconds(5), ChatImagesTests.Png(64, 36)),
            new VideoFrames.Frame(TimeSpan.FromSeconds(15), [1, 2, 3, 4]),
            new VideoFrames.Frame(TimeSpan.FromSeconds(25), ChatImagesTests.Png(64, 36)),
        };
        var sheet = VideoFrames.Compose(frames)!.Value;
        Assert.Equal(2, sheet.Frames);
        var image = StbImageSharp.ImageResult.FromMemory(sheet.Image, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        var reference = StbImageSharp.ImageResult.FromMemory(
            VideoFrames.Compose([frames[0], new VideoFrames.Frame(TimeSpan.FromSeconds(25), ChatImagesTests.Png(64, 36))])!.Value.Image,
            StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        Assert.Equal(reference.Data, image.Data);
    }

    [Fact]
    public void AFrameOfAnExtremeShape_IsFitted_NeverDrawnItsFullHeight()
    {
        var sheet = VideoFrames.Compose([new VideoFrames.Frame(TimeSpan.Zero, ChatImagesTests.Png(2, 4000))])!.Value;
        var image = StbImageSharp.ImageResult.FromMemory(sheet.Image, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        Assert.Equal(476 * 2, image.Height);
        Assert.Equal(476, image.Width);
    }

    [Fact]
    public void TheSheet_WritesEachFramesTime_AndLaysThemFourToARow()
    {
        var frames = Enumerable.Range(0, 8).Select(i => new VideoFrames.Frame(TimeSpan.FromSeconds(i * 5 + 3), ChatImagesTests.Png(64, 36))).ToList();
        var sheet = VideoFrames.Compose(frames)!.Value;
        Assert.Equal(8, sheet.Frames);
        var image = StbImageSharp.ImageResult.FromMemory(sheet.Image, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        Assert.Equal(4 * 476 + 3 * 4, image.Width);
        Assert.Equal(2 * 268 + 4, image.Height);
        // The time's box is drawn in the top-left corner of each frame: black around white strokes.
        Assert.True(image.Data[(8 * image.Width + 8) * 4] < 40, "no time box on the first frame");
    }

    [Theory]
    [InlineData(3, "0:03")]
    [InlineData(65, "1:05")]
    [InlineData(3725, "1:02:05")]
    public void ATime_IsWrittenAsMinutesAndSeconds(int seconds, string text) =>
        Assert.Equal(text, VideoFrames.Time(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void AVideo_IsLargerThanAnImage_AndBothEditorsAttachIt()
    {
        Assert.True(ChatImages.Handles("bug.mp4") && ChatImages.Handles("rec.webm") && ChatImages.Handles("clip.mov"));
        Assert.Equal(VideoFrames.MaxBytes, ChatImages.MaxBytesOf("bug.mp4"));
        Assert.Equal(ChatImages.MaxBytes, ChatImages.MaxBytesOf("shot.png"));
        var root = ConversationPersistenceSilenceTests.RepoRoot();
        var provider = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(Path.Combine(root, "vscode", "src", "chatViewProvider.ts")));
        foreach (var extension in VideoFrames.Extensions)
            Assert.Contains("|" + extension.TrimStart('.'), provider[provider.IndexOf("const IMAGE_FILE", StringComparison.Ordinal)..][..200]);
        Assert.Contains("return 100 * 1024 * 1024;", File.ReadAllText(Path.Combine(root, "vscode", "src", "droppedFiles.ts")));
    }
}
