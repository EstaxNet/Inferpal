using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Documents;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An image in the question (pari 84): recognised by its bytes, refused and said when the servers do not decode it or it is
/// too large, sent as Ollama's base64 or as OpenAI content parts, kept through the prompted-tools rewrite (Gemma 4 has no
/// tool template), and sent with its question only.
/// </summary>
public sealed class ChatImagesTests
{
    /// <summary>A valid PNG of <paramref name="width"/> × <paramref name="height"/>: signature, IHDR, one IDAT, IEND.</summary>
    internal static byte[] Png(int width, int height)
    {
        static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, data.Length); s.Write(len);
            var td = Encoding.ASCII.GetBytes(type).Concat(data).ToArray(); s.Write(td);
            var crc = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(td)); s.Write(crc);
        }
        var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; ihdr[9] = 0;   // 8-bit greyscale
        Chunk(png, "IHDR", ihdr);
        var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            for (var y = 0; y < height; y++) { z.WriteByte(0); z.Write(new byte[width]); }
        Chunk(png, "IDAT", raw.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    [Fact]
    public void APng_IsReadWithItsSize_AndALargeOne_IsSaid()
    {
        var small = ChatImages.Read(Png(120, 40), "capture.png");
        Assert.Null(small.Refusal);
        Assert.Equal(("image/png", 120, 40), (small.Image!.MediaType, small.Image.Width, small.Image.Height));
        Assert.Null(small.Note);

        // Larger than 1920 x 1080: reduced to fit, a decodable PNG again, and said.
        var large = ChatImages.Read(Png(2400, 30), "maquette.png");
        Assert.Equal((1920, 24), (large.Image!.Width, large.Image.Height));
        Assert.Equal(("image/png", 1920, 24), Size(ChatImages.Sniff(Convert.FromBase64String(large.Image.Base64))));
        Assert.Equal(Strings.ImageScaledNote("maquette.png", 2400, 30, 1920, 24), large.Note);
    }

    /// <summary>A BMP becomes a PNG; an image declaring more pixels than Inferpal reads is refused before anything decodes
    /// it (both measured libraries went past a gigabyte on one); a large image that does not decode is said damaged.</summary>
    [Fact]
    public void ABmpIsConverted_AHugeImageIsRefusedUndecoded_ADamagedOneIsSaid()
    {
        var bmp = new byte[54 + 4 * 2 * 3];
        "BM"u8.CopyTo(bmp);
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(54).CopyTo(bmp, 10);
        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(4).CopyTo(bmp, 18);
        BitConverter.GetBytes(2).CopyTo(bmp, 22);
        BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
        BitConverter.GetBytes((short)24).CopyTo(bmp, 28);
        var read = ChatImages.Read(bmp, "capture.bmp");
        Assert.Null(read.Refusal);
        Assert.Equal(("image/png", 4, 2), (read.Image!.MediaType, read.Image.Width, read.Image.Height));

        // A PNG header declaring 30 000 x 30 000 pixels, nothing else: refused on its header.
        var huge = Png(1, 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(huge.AsSpan(16), 30_000);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(huge.AsSpan(20), 30_000);
        Assert.Equal(Strings.ImageTooManyPixels("bombe.png", 30_000, 30_000), ChatImages.Read(huge, "bombe.png").Refusal);

        var cut = Png(2400, 1400);
        Assert.Equal(Strings.ImageDamaged("coupee.png"), ChatImages.Read(cut[..(cut.Length / 3)], "coupee.png").Refusal);
    }

    [Fact]
    public void JpegGifAndWebp_AreRecognisedByTheirBytes_NotTheirName()
    {
        byte[] gif = [.. "GIF89a"u8, 0x40, 0x01, 0xC8, 0x00, 0, 0, 0];
        Assert.Equal(("image/gif", 320, 200), Size(ChatImages.Sniff(gif)));
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0, 0, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x02, 0x58, 0x03, 0x20, 0, 0, 0];
        Assert.Equal(("image/jpeg", 800, 600), Size(ChatImages.Sniff(jpeg)));
        byte[] webp = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBPVP8X"u8, 10, 0, 0, 0, 0, 0, 0, 0, 0x7F, 0x07, 0x00, 0x37, 0x04, 0x00];
        Assert.Equal(("image/webp", 1920, 1080), Size(ChatImages.Sniff(webp)));
        // A PNG named .jpg is a PNG.
        Assert.Equal("image/png", ChatImages.Read(Png(10, 10), "photo.jpg").Image!.MediaType);
    }

    private static (string?, int, int) Size((string? MediaType, int Width, int Height) s) => (s.MediaType, s.Width, s.Height);

    [Fact]
    public void AFormatTheServersDoNotDecode_OrATooLargeFile_IsNotAttached_AndSaysWhy()
    {
        var heic = ChatImages.Read([0, 0, 0, 0x18, .. "ftypheic"u8, 0, 0, 0, 0], "photo-ecran.heic");
        Assert.Null(heic.Image);
        Assert.Equal(Strings.ImageFormatNotSent("photo-ecran.heic", "HEIC"), heic.Refusal);
        var big = ChatImages.Read(new byte[ChatImages.MaxBytes + 1], "big.png");
        Assert.Equal(Strings.ImageTooLarge("big.png", 10), big.Refusal);
    }

    [Fact]
    public void AnImage_IsSentWithItsQuestionOnly_AndAnEarlierQuestion_SaysItsImagesWereSentThen()
    {
        var image = Convert.ToBase64String(Png(4, 4));
        List<ChatMessageDto> history =
        [
            new("user", "Pourquoi le build échoue ?", Images: [image]),
            new("assistant", "Il manque un point-virgule."),
            new("user", "Et maintenant ?"),
            new("user", "[plan]") { IsScaffolding = true },
        ];
        var sent = ChatImages.OnlyWithTheirQuestion(history);
        Assert.Null(sent[0].Images);
        Assert.Contains("1 image(s) were attached to this earlier question", sent[0].Content);
        // The latest question is the person's last one, not a step the run added: its images stay.
        List<ChatMessageDto> run = [new("user", "Lis cette capture", Images: [image]), new("user", "[observe]") { IsScaffolding = true }];
        Assert.Equal([image], ChatImages.OnlyWithTheirQuestion(run)[0].Images);
    }

    [Fact]
    public void TheOpenAiShape_IsContentParts_AndThePromptedToolsRewrite_KeepsTheImages()
    {
        var image = Convert.ToBase64String(Png(4, 4));
        var mapped = Services.Inference.OpenAiCompatibleClient.MapMessages([new("user", "Lis ceci", Images: [image])]);
        var json = JsonSerializer.Serialize(mapped[0]);
        Assert.Contains("\"type\":\"text\",\"text\":\"Lis ceci\"", json);
        Assert.Contains("\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64," + image, json);

        // Gemma 4 has no tool template: its tools are prompted, and its question keeps its image.
        var tools = new List<ToolDefinition> { new("function", new ToolFunction("read_file", "Reads a file.", new { type = "object" })) };
        var ollama = Services.Inference.PromptedTools.Rewrite([new ChatMessageDto("user", "Lis ceci", Images: [image])], tools);
        Assert.Equal([image], ollama.Single(m => m.Role == "user").Images);
        var openAi = JsonSerializer.Serialize(Services.Inference.PromptedTools.Rewrite(mapped, tools));
        Assert.Contains("data:image/png;base64," + image, openAi);
    }

    [Fact]
    public void LmStudio_PublishesWhetherAModelSees()
    {
        using var qwen = JsonDocument.Parse("""{"vision": true, "trained_for_tool_use": true}""");
        using var granite = JsonDocument.Parse("""{"vision": false}""");
        using var none = JsonDocument.Parse("""{}""");
        Assert.True(Services.Inference.LmStudioClient.VisionOf(qwen.RootElement));
        Assert.False(Services.Inference.LmStudioClient.VisionOf(granite.RootElement));
        Assert.Null(Services.Inference.LmStudioClient.VisionOf(none.RootElement));
    }

    /// <summary>The card before sending, for a model that does not see: it offers another model of the server that does, or
    /// none when no model does, and always sending without the images.</summary>
    [Fact]
    public void TheNotSeenCard_OffersAModelThatSees_OrOnlySendingWithout()
    {
        var card = ImageCards.NotSeen("granite-4.2-8b", ["capture.png"], ["qwen/qwen3.8-27b", "gemma-4-12b-it-qat"]);
        Assert.Equal(Strings.ImageNotSeenTitle("granite-4.2-8b"), card.Title);
        Assert.Equal("qwen/qwen3.8-27b", card.OtherModel);
        Assert.Equal(Strings.ImageAskOther("qwen/qwen3.8-27b"), card.AskOtherLabel);
        Assert.Equal(Strings.ImageVisionModels("qwen/qwen3.8-27b, gemma-4-12b-it-qat"), card.VisionModels);

        var none = ImageCards.NotSeen("granite-4.2-8b", ["capture.png"], []);
        Assert.Equal("", none.OtherModel);
        Assert.Equal("", none.AskOtherLabel);
        Assert.Equal(Strings.ImageNoVisionModel, none.VisionModels);
        Assert.Equal(Strings.ImageSendWithout, none.SendWithoutLabel);

        var chip = ImageCards.Chip(ChatImages.Read(Png(1100, 260), "capture.png").Image!, pasted: true, seenBy: "granite-4.2-8b");
        Assert.StartsWith("1100 × 260 · ", chip.Detail);
        Assert.EndsWith(" · " + Strings.ImagePasted, chip.Detail);
        Assert.Equal(Strings.ImageNotSeenChip("granite-4.2-8b"), chip.Warning);
        Assert.Equal(Strings.ImageSeesBadge, ImageCards.Badge(true));
        Assert.Equal("", ImageCards.Badge(null));
    }
}
