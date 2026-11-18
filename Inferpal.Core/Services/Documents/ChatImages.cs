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
    /// <remarks>Not <c>.svg</c>: an SVG is text, and is attached as its markup.</remarks>
    internal static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".heic", ".heif", ".tif", ".tiff"];

    /// <summary>Whether <paramref name="path"/> names an image file, by its extension: what decides to read it as an image
    /// rather than as a text or a document.</summary>
    internal static bool Handles(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>The most pixels a converted photo may have: a phone's full resolution (48 MP), scaled down when sent.</summary>
    internal const long MaxConvertedPixels = 64_000_000;

    /// <summary>
    /// <see cref="Read"/>, and a HEIC photo (no server decodes one) converted by this system's decoder first, then sent as a
    /// JPEG; why it could not be is said with what installs the decoder.
    /// </summary>
    internal static async Task<ChatImageRead> ReadAsync(byte[] bytes, string name, CancellationToken ct)
    {
        if (bytes.LongLength > MaxBytes || !HeicImage.Is(bytes)) return Read(bytes, name);
        var (converted, why) = await HeicImage.ConvertAsync(bytes, ct).ConfigureAwait(false);
        if (converted is null) return new(null, Strings.ImageNotDecoded(name, why ?? "it could not be converted"), null);
        // Its size read from its header before a pixel is decoded; a phone's full-resolution photo (48 MP) passes, since it
        // is scaled down anyway.
        var (type, width, height) = Sniff(converted);
        if (type is null) return new(null, Strings.ImageDamaged(name), null);
        if ((long)width * height > MaxConvertedPixels) return new(null, Strings.ImageTooManyPixels(name, width, height), null);
        StbImageSharp.ImageResult image;
        try { image = StbImageSharp.ImageResult.FromMemory(converted, StbImageSharp.ColorComponents.RedGreenBlueAlpha); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            Diagnostics.Swallow("ChatImages.Heic", ex);
            return new(null, Strings.ImageDamaged(name), null);
        }
        // macOS converts to a JPEG that keeps the photo's EXIF orientation: applied here, as for any JPEG.
        var (pixels, w0, h0) = type == "image/jpeg" ? Orient(image.Data, image.Width, image.Height, JpegOrientation(converted))
                                                    : (image.Data, image.Width, image.Height);
        var drawn = Encode(pixels, w0, h0, LargeWidth, LargeHeight, jpeg: true);
        var scaled = drawn.Width != w0 || drawn.Height != h0;
        return new(new ChatImage(name, Convert.ToBase64String(drawn.Data), drawn.MediaType, drawn.Width, drawn.Height, drawn.Data.LongLength),
                   null, scaled ? Strings.ImageScaledNote(name, w0, h0, drawn.Width, drawn.Height) : null);
    }

    /// <summary>The image in <paramref name="bytes"/>, named <paramref name="name"/>, or why it is not attached.</summary>
    internal static ChatImageRead Read(byte[] bytes, string name)
    {
        if (bytes.LongLength > MaxBytes)
            return new(null, Strings.ImageTooLarge(name, (int)(MaxBytes / (1024 * 1024))), null);
        var (mediaType, width, height) = Sniff(bytes);
        var bmp = mediaType is null && bytes.Length >= 26 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M';
        if (bmp)
        {
            // A BMP's height is signed (negative = drawn top-down); int.MinValue has no positive value.
            var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22));
            width  = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18));
            height = rawHeight == int.MinValue ? -1 : Math.Abs(rawHeight);
        }
        // A TIFF no server decodes: drawn here, sent as a PNG — its first page, the others said.
        if (mediaType is null && !bmp && TiffImage.Is(bytes))
        {
            var tiff = TiffImage.Decode(bytes, MaxPixels);
            if (tiff.Rgba is null) return new(null, Strings.ImageNotDecoded(name, tiff.Refusal ?? "it is damaged"), null);
            var drawn = Encode(tiff.Rgba, tiff.Width, tiff.Height, LargeWidth, LargeHeight, jpeg: false);
            var notes = new List<string>();
            if (drawn.Width != tiff.Width || drawn.Height != tiff.Height)
                notes.Add(Strings.ImageScaledNote(name, tiff.Width, tiff.Height, drawn.Width, drawn.Height));
            if (tiff.Pages > 1) notes.Add(Strings.ImageFirstPageOnly(name, tiff.Pages));
            return new(new ChatImage(name, Convert.ToBase64String(drawn.Data), drawn.MediaType, drawn.Width, drawn.Height, drawn.Data.LongLength), null,
                       notes.Count > 0 ? string.Join(" ", notes) : null);
        }
        if (mediaType is null && !bmp)
        {
            var extension = Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
            return new(null, Strings.ImageFormatNotSent(name, extension.Length > 0 ? extension : "?"), null);
        }
        // A header read as signed (PNG, BMP) can declare a negative size, which would pass the pixel cap.
        if (width < 0 || height < 0 || bmp && (width == 0 || height == 0))
            return new(null, Strings.ImageDamaged(name), null);
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
        // A JPEG's EXIF orientation (a phone photo stored sideways) is applied: written again, the tag is gone.
        var (source, sw, sh) = jpeg ? Orient(image.Data, image.Width, image.Height, JpegOrientation(bytes))
                                    : (image.Data, image.Width, image.Height);
        return Encode(source, sw, sh, LargeWidth, LargeHeight, jpeg);
    }

    /// <summary>The pixels (RGBA) fitted into <paramref name="boxW"/> × <paramref name="boxH"/> and written as a JPEG or a PNG.</summary>
    private static (byte[] Data, string MediaType, int Width, int Height) Encode(byte[] source, int sw, int sh, int boxW, int boxH, bool jpeg)
    {
        var scale = Math.Min(1.0, Math.Min((double)boxW / sw, (double)boxH / sh));
        int w = Math.Max(1, (int)(sw * scale)), h = Math.Max(1, (int)(sh * scale));
        var pixels = source;
        if (w != sw || h != sh)
        {
            pixels = new byte[w * h * 4];
            StbImageResizeSharp.StbImageResize.stbir_resize_uint8(source, sw, sh, sw * 4, pixels, w, h, w * 4, 4);
        }
        using var output = new MemoryStream();
        var writer = new StbImageWriteSharp.ImageWriter();
        if (jpeg) writer.WriteJpg(pixels, w, h, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, output, 85);
        else writer.WritePng(pixels, w, h, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, output);
        return (output.ToArray(), jpeg ? "image/jpeg" : "image/png", w, h);
    }

    /// <summary>
    /// A small PNG of the image (240 × 160 at most), as a data URL, to draw under the question — a full image redrawn with
    /// every refresh of the chat would carry megabytes each time. <c>null</c> when it does not decode here (a WebP).
    /// </summary>
    internal static string? Thumbnail(ChatImage image)
    {
        try
        {
            var decoded = StbImageSharp.ImageResult.FromMemory(Convert.FromBase64String(image.Base64), StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            var (data, _, _, _) = Encode(decoded.Data, decoded.Width, decoded.Height, 240, 160, jpeg: false);
            return "data:image/png;base64," + Convert.ToBase64String(data);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or OverflowException or FormatException)
        {
            Diagnostics.Swallow("ChatImages.Thumbnail", ex);
            return null;
        }
    }

    /// <summary>A JPEG's EXIF orientation (1 to 8), 1 when it has none or it cannot be read.</summary>
    internal static int JpegOrientation(byte[] b)
    {
        var at = 2;
        while (at + 4 < b.Length && b[at] == 0xFF)
        {
            var marker = b[at + 1];
            var length = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at + 2));
            if (length < 2 || marker == 0xDA) return 1;
            var exif = at + 4;
            if (marker == 0xE1 && exif + 14 <= b.Length && b[exif] == (byte)'E' && b[exif + 1] == (byte)'x' && b[exif + 2] == (byte)'i' && b[exif + 3] == (byte)'f')
            {
                var tiff = exif + 6;
                var little = b[tiff] == (byte)'I';
                int U16(int o) => o + 2 <= b.Length ? (little ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o))) : 0;
                long U32(int o) => o + 4 <= b.Length ? (little ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o))) : 0;
                var ifd = tiff + U32(tiff + 4);
                if (ifd < tiff || ifd + 2 > b.Length) return 1;
                var entries = U16((int)ifd);
                for (var e = 0; e < entries; e++)
                {
                    var entry = (int)ifd + 2 + e * 12;
                    if (entry + 12 > b.Length) return 1;
                    if (U16(entry) == 0x0112) return U16(entry + 8) is var o and >= 1 and <= 8 ? o : 1;
                }
                return 1;
            }
            at += 2 + length;
        }
        return 1;
    }

    /// <summary>RGBA pixels turned upright for an EXIF orientation: 2-4 mirror or turn half way, 5-8 swap the sides.</summary>
    internal static (byte[] Pixels, int Width, int Height) Orient(byte[] src, int w, int h, int orientation)
    {
        if (orientation is < 2 or > 8) return (src, w, h);
        var swap = orientation >= 5;
        int ow = swap ? h : w, oh = swap ? w : h;
        var dst = new byte[src.Length];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (dx, dy) = orientation switch
                {
                    2 => (w - 1 - x, y),
                    3 => (w - 1 - x, h - 1 - y),
                    4 => (x, h - 1 - y),
                    5 => (y, x),
                    6 => (h - 1 - y, x),
                    7 => (h - 1 - y, w - 1 - x),
                    _ => (y, w - 1 - x),
                };
                Buffer.BlockCopy(src, (y * w + x) * 4, dst, (dy * ow + dx) * 4, 4);
            }
        return (dst, ow, oh);
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

    /// <summary>The most tokens an image sent is counted for: one per 1 024 pixels of the largest box an image is reduced to
    /// fit.</summary>
    /// <remarks>Read off the servers' own counts (docs/probes/pari84 runs, prompt tokens of the image arm): Qwen3.8 and Ornith
    /// spend about a token per thousand pixels, Gemma 4 half of that — the larger is kept, so a request is never
    /// under-counted.</remarks>
    internal const int TokensPerImageCeiling = LargeWidth * LargeHeight / 1024;

    /// <summary>The tokens an image weighs in a request, from the size its header declares; the ceiling when the header does
    /// not say (WebP, a JPEG whose frame comes late) or cannot be read.</summary>
    internal static int EstimatedTokens(string base64)
    {
        try
        {
            // The head only: a header is in the first bytes, and the whole image is never decoded to be counted.
            var take = Math.Min(base64.Length, 4 * (64 * 1024 / 3)) & ~3;
            var (_, width, height) = Sniff(Convert.FromBase64String(base64[..take]));
            if (width > 0 && height > 0)
                return (int)Math.Min(TokensPerImageCeiling, (long)width * height / 1024 + 16);
        }
        catch (FormatException) { }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException) { }
        return TokensPerImageCeiling;
    }

    /// <summary>
    /// Retires the images of the questions already in <paramref name="history"/>, before a new question joins it: each keeps
    /// a line saying its images were sent then. Done when the question ENTERS the history, never guessed per request: the
    /// run's own messages (the plan's request, an observation, the synthesis) are user messages after the question, and a
    /// rule that kept images on "the latest user message" took them off the question for the whole run.
    /// </summary>
    /// <remarks>Every request of the run carries the question's images: a model keeps nothing between requests.</remarks>
    /// <param name="before">How many messages, from the start, are earlier ones; all of them when not given.</param>
    internal static void RetireEarlier(List<ChatMessageDto> history, int? before = null)
    {
        for (var i = 0; i < (before ?? history.Count); i++)
            if (history[i].Images is { Count: > 0 } images)
                history[i] = history[i] with
                {
                    Images = null,
                    Content = (history[i].Content ?? string.Empty) + string.Create(CultureInfo.InvariantCulture,
                        $"\n[{images.Count} image(s) were attached to this earlier question; they are not sent again.]"),
                };
    }
}
