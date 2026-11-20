using System.Collections.Concurrent;
using System.Globalization;
using Inferpal.Localization;

namespace Inferpal.Services.Presentation;

/// <summary>A binary file among a run's changes: what it is and its size, never lines; an image, itself.</summary>
/// <param name="Label">What its line says in place of the line counts: "PNG image · 1280 × 800 · 48 KB",
/// "binary file · 18 KB → 19 KB · no line view".</param>
/// <param name="Before">The image before the run as a small PNG data URL; <c>null</c> for none (not an image, not
/// decodable here — a WebP —, or past the size and pixel limits). Both editors draw this same picture.</param>
/// <param name="After">The image now, the same way.</param>
/// <param name="BeforeCaption">"before · 700 × 650", under its picture; empty without one.</param>
/// <param name="AfterCaption">"now · 700 × 650".</param>
internal sealed record RunFileBinary(string Label, string? Before, string? After, string BeforeCaption, string AfterCaption)
{
    /// <summary>Whether there is a picture to show when the file is unfolded; a binary file without one has nothing
    /// under its line.</summary>
    public bool HasPictures => Before is not null || After is not null;
}

/// <summary>
/// Tells a binary file of a run from a text one, by the reader every other tool uses (<c>TextFileEncoding.IsBinary</c>:
/// a NUL in its head), and says it. ⚠ Read as text, an image or a workbook gives line counts that mean nothing.
/// </summary>
internal static class RunBinary
{
    /// <summary>Head read to tell binary from text and an image's kind and size.</summary>
    private const int HeadBytes = 64 * 1024;

    /// <summary>Thumbnails already drawn, by file, length and write time: a card is drawn again at every Keep or Undo.</summary>
    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime Written), string?> Drawn = new();

    /// <summary>A binary file's description, or <c>null</c> when no side of it is binary.</summary>
    /// <param name="beforePath">Its copy from before the run; <c>null</c> when there is none (created, or no copy kept).</param>
    /// <param name="afterPath">The file now; <c>null</c> when it is gone.</param>
    /// <param name="created">The run created the file: only then is a lone "after" said as new.</param>
    /// <param name="pictures">Whether to draw the images' thumbnails (a card), or only say them (the bar, a text).</param>
    /// <remarks>A side that is text does not count: a text placeholder replaced by a PNG is said as the PNG alone.</remarks>
    internal static RunFileBinary? Of(string? beforePath, string? afterPath, bool created, bool pictures)
    {
        var beforeSide = Side(beforePath);
        var afterSide = Side(afterPath);
        var before = beforeSide is { Binary: true } ? beforeSide : null;
        var after = afterSide is { Binary: true } ? afterSide : null;
        if (before is null && after is null) return null;

        // The kind is the file's as it is now: an image overwritten with text is no image any more.
        var kind = (afterSide is not null ? after?.MediaType : before?.MediaType) switch
        {
            "image/png" => "PNG", "image/jpeg" => "JPEG", "image/gif" => "GIF", "image/webp" => "WebP", _ => null,
        };
        string label;
        if (kind is null)
            label = beforeSide is not null && afterSide is not null
                ? Strings.RunBinaryFileChanged(ImageCards.SizeText(beforeSide.Bytes), ImageCards.SizeText(afterSide.Bytes))
                : Strings.RunBinaryFileOne(ImageCards.SizeText((afterSide ?? beforeSide)!.Bytes));
        else if (before is not null && after is not null)
            label = Strings.RunBinaryImageChanged(kind, ImageCards.SizeText(before.Bytes), ImageCards.SizeText(after.Bytes));
        else if (created && after is { Width: > 0 })
            label = Strings.RunBinaryImageNew(kind, Dim(after.Width), Dim(after.Height), ImageCards.SizeText(after.Bytes));
        else
            label = Strings.RunBinaryImageOne(kind, ImageCards.SizeText((after ?? before)!.Bytes));

        if (kind is null || !pictures) return new RunFileBinary(label, null, null, string.Empty, string.Empty);
        var drawBefore = before is not null ? Thumbnail(beforePath!, before) : null;
        var drawAfter = after is not null ? Thumbnail(afterPath!, after) : null;
        return new RunFileBinary(label, drawBefore, drawAfter,
                                 drawBefore is not null ? Strings.RunBinaryBefore(Dim(before!.Width), Dim(before.Height)) : string.Empty,
                                 drawAfter is not null ? Strings.RunBinaryNow(Dim(after!.Width), Dim(after.Height)) : string.Empty);
    }

    private sealed record SideInfo(bool Binary, long Bytes, string? MediaType, int Width, int Height, DateTime Written);

    /// <summary>One side of the file: <c>null</c> when it does not exist or cannot be read.</summary>
    private static SideInfo? Side(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var head = new byte[(int)Math.Min(fs.Length, HeadBytes)];
            fs.ReadExactly(head);
            var written = File.GetLastWriteTimeUtc(path);
            if (!Tools.TextFileEncoding.IsBinary(head)) return new SideInfo(false, fs.Length, null, 0, 0, written);
            var (type, width, height) = Documents.ChatImages.Sniff(head);
            return new SideInfo(true, fs.Length, type, Math.Max(0, width), Math.Max(0, height), written);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("RunBinary.Side", ex);
            return null;
        }
    }

    /// <summary>A small PNG of the image; <c>null</c> past the limits an attached image has (bytes, and the pixels its
    /// header declares: a few kilobytes can declare a picture whose decoding takes gigabytes).</summary>
    private static string? Thumbnail(string path, SideInfo side)
    {
        // A WebP is not decoded here (as for an attached one): said, never read whole for nothing.
        if (side.MediaType == "image/webp" || side.Bytes > Documents.ChatImages.MaxBytes || side.Width <= 0 || side.Height <= 0
            || (long)side.Width * side.Height > Documents.ChatImages.MaxPixels)
            return null;
        if (Drawn.Count > 256) Drawn.Clear();
        return Drawn.GetOrAdd((path, side.Bytes, side.Written), _ =>
        {
            try { return Documents.ChatImages.ThumbnailOf(File.ReadAllBytes(path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Diagnostics.Swallow("RunBinary.Thumbnail", ex);
                return null;
            }
        });
    }

    private static string Dim(int value) => value.ToString(CultureInfo.InvariantCulture);
}
