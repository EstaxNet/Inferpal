using System.Buffers.Binary;
using System.Globalization;
using Inferpal.Localization;
using Inferpal.Models;

namespace Inferpal.Services.Documents;

/// <summary>An image the person attached to a question: its name, its bytes as base64, its format and size in pixels.</summary>
/// <param name="Width">0 when the header could not be read (the image is still sent: the server decodes it).</param>
internal sealed record ChatImage(string Name, string Base64, string MediaType, int Width, int Height, long Bytes);

/// <summary>What reading an image gave: the image, or why it is not attached (in the person's language), and a note when
/// it is attached but large.</summary>
internal sealed record ChatImageRead(ChatImage? Image, string? Refusal, string? Note);

/// <summary>
/// The one reader of the images a person attaches to a question, for every editor: the formats the servers decode (PNG,
/// JPEG, WebP, GIF), recognised by their bytes, never by the extension; a size limit; how they travel — base64 for
/// Ollama, content parts for an OpenAI-compatible server; and sent with their question only.
/// </summary>
/// <remarks>
/// A large image is reduced to fit 1920 × 1080 and a BMP converted to PNG, by the C# ports of the stb headers (public
/// domain or MIT, no native code; measured against SkiaSharp in docs/probes/pari84). ⚠ No image library bounds itself — a
/// PNG declaring 30 000 × 30 000 pixels in a few KB took both measured ones past a gigabyte —, so the size its header
/// declares is refused past <see cref="MaxPixels"/> before anything decodes it. A WebP, which stb does not read, is sent as
/// it is and said large.
/// </remarks>
internal static class ChatImages
{
    /// <summary>The largest image file sent, in bytes.</summary>
    internal const long MaxBytes = 10L * 1024 * 1024;

    /// <summary>Images one question may carry.</summary>
    internal const int MaxPerQuestion = 4;

    /// <summary>The box a large image is reduced to fit.</summary>
    private const int LargeWidth = 1920, LargeHeight = 1080;

    /// <summary>The most pixels an image may declare: past it, nothing decodes it (4 bytes each, decoded).</summary>
    internal const long MaxPixels = 40_000_000;

    /// <summary>Extensions the attach pickers offer as images.</summary>
    internal static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".heic", ".heif", ".tif", ".tiff", ".svg"];

    /// <summary>Whether <paramref name="path"/> names an image file, by its extension: what decides to read it as an image
    /// rather than as a text or a document.</summary>
    internal static bool Handles(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>The image in <paramref name="bytes"/>, named <paramref name="name"/>, or why it is not attached.</summary>
    internal static ChatImageRead Read(byte[] bytes, string name)
    {
        if (bytes.LongLength > MaxBytes)
            return new(null, Strings.ImageTooLarge(name, (int)(MaxBytes / (1024 * 1024))), null);
        var (mediaType, width, height) = Sniff(bytes);
        var bmp = mediaType is null && bytes.Length >= 26 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M';
        if (bmp) (width, height) = (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18)), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22))));
        if (mediaType is null && !bmp)
        {
            var extension = Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
            return new(null, Strings.ImageFormatNotSent(name, extension.Length > 0 ? extension : "?"), null);
        }
        if ((long)width * height > MaxPixels)
            return new(null, Strings.ImageTooManyPixels(name, width, height), null);
        var large = width > LargeWidth || height > LargeHeight;
        if (!bmp && !large || mediaType == "image/webp")
        {
            var image = new ChatImage(name, Convert.ToBase64String(bytes), mediaType!, width, height, bytes.LongLength);
            return new(image, null, large ? Strings.ImageLargeNote(name, width, height) : null);
        }
        // A BMP becomes a PNG; a large PNG, JPEG or GIF is reduced to fit the box — a JPEG stays a JPEG.
        var reduced = Reduce(bytes, jpeg: mediaType == "image/jpeg");
        if (reduced is null) return new(null, Strings.ImageDamaged(name), null);
        var (data, type, w, h) = reduced.Value;
        return new(new ChatImage(name, Convert.ToBase64String(data), type, w, h, data.LongLength), null,
                   large ? Strings.ImageScaledNote(name, width, height, w, h) : null);
    }

    /// <summary>The image decoded, fitted into <see cref="LargeWidth"/> × <see cref="LargeHeight"/> and written again;
    /// null when it does not decode (a damaged file).</summary>
    private static (byte[] Data, string MediaType, int Width, int Height)? Reduce(byte[] bytes, bool jpeg)
    {
        StbImageSharp.ImageResult image;
        try { image = StbImageSharp.ImageResult.FromMemory(bytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            Diagnostics.Swallow("ChatImages.Decode", ex);
            return null;
        }
        var scale = Math.Min(1.0, Math.Min((double)LargeWidth / image.Width, (double)LargeHeight / image.Height));
        int w = Math.Max(1, (int)(image.Width * scale)), h = Math.Max(1, (int)(image.Height * scale));
        var pixels = image.Data;
        if (w != image.Width || h != image.Height)
        {
            pixels = new byte[w * h * 4];
            StbImageResizeSharp.StbImageResize.stbir_resize_uint8(image.Data, image.Width, image.Height, image.Width * 4, pixels, w, h, w * 4, 4);
        }
        using var output = new MemoryStream();
        var writer = new StbImageWriteSharp.ImageWriter();
        if (jpeg) writer.WriteJpg(pixels, w, h, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, output, 85);
        else writer.WritePng(pixels, w, h, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, output);
        return (output.ToArray(), jpeg ? "image/jpeg" : "image/png", w, h);
    }

    /// <summary>The format and size of an image from its first bytes; a null format for anything the servers do not decode
    /// (HEIC, TIFF, BMP, SVG…).</summary>
    internal static (string? MediaType, int Width, int Height) Sniff(byte[] b)
    {
        if (b.Length >= 24 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G')
            return ("image/png", BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(20)));
        if (b.Length >= 10 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F')
            return ("image/gif", BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6)), BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8)));
        if (b.Length >= 30 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
            && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P')
        {
            var size = WebpSize(b);
            return ("image/webp", size?.Item1 ?? 0, size?.Item2 ?? 0);
        }
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
        {
            var size = JpegSize(b);
            return ("image/jpeg", size?.Item1 ?? 0, size?.Item2 ?? 0);
        }
        return (null, 0, 0);
    }

    private static (int, int)? WebpSize(byte[] b)
    {
        var chunk = System.Text.Encoding.ASCII.GetString(b, 12, 4);
        if (chunk == "VP8X" && b.Length >= 30) return (1 + (b[24] | b[25] << 8 | b[26] << 16), 1 + (b[27] | b[28] << 8 | b[29] << 16));
        if (chunk == "VP8 " && b.Length >= 30) return (BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(26)) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(28)) & 0x3FFF);
        if (chunk == "VP8L" && b.Length >= 25)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(21));
            return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }
        return null;
    }

    /// <summary>A JPEG's size from its first start-of-frame marker.</summary>
    private static (int, int)? JpegSize(byte[] b)
    {
        var at = 2;
        while (at + 9 < b.Length)
        {
            if (b[at] != 0xFF) return null;
            var marker = b[at + 1];
            var length = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at + 2));
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return (BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at + 7)), BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at + 5)));
            if (length < 2) return null;
            at += 2 + length;
        }
        return null;
    }

    /// <summary>A data URL of a base64 image, its media type read from its bytes.</summary>
    internal static string DataUrl(string base64)
    {
        var head = Convert.FromBase64String(base64.Length > 64 ? base64[..64] : base64);
        var mediaType = Sniff(head).MediaType ?? "image/png";
        return "data:" + mediaType + ";base64," + base64;
    }

    /// <summary>A question's content as OpenAI-compatible content parts: its text, then each image as a data URL.</summary>
    internal static List<object> Parts(string text, IReadOnlyList<string> images)
    {
        var parts = new List<object> { new { type = "text", text } };
        foreach (var image in images) parts.Add(new { type = "image_url", image_url = new { url = DataUrl(image) } });
        return parts;
    }

    /// <summary>
    /// The conversation with each question's images kept on that question only while it is the latest one the person
    /// asked; an earlier question keeps a line saying its images were sent then — a follow-up does not send every
    /// screenshot again, nor does each step of a run send it twice over (the run's own messages are no questions).
    /// </summary>
    internal static List<ChatMessageDto> OnlyWithTheirQuestion(List<ChatMessageDto> messages)
    {
        var latest = messages.FindLastIndex(m => m.Role == "user" && !m.IsScaffolding && !m.IsUserNote);
        if (!messages.Where((m, i) => i != latest && m.Images is { Count: > 0 }).Any()) return messages;
        return [.. messages.Select((m, i) => i == latest || m.Images is not { Count: > 0 } images ? m : m with
        {
            Images = null,
            Content = (m.Content ?? string.Empty) + string.Create(CultureInfo.InvariantCulture,
                $"\n[{images.Count} image(s) were attached to this earlier question; they are not sent again.]"),
        })];
    }
}
