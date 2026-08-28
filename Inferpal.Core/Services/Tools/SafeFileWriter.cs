using System.IO;
using System.Text;

namespace Inferpal.Services.Tools;

/// <summary>
/// The one way the editing tools write a text file: re-encoded exactly as it was found.
/// </summary>
/// <remarks>
/// <c>File.WriteAllTextAsync(path, content)</c> always emits UTF-8 without BOM — so every
/// one-line <c>apply_diff</c> stripped the BOM Visual Studio puts on .cs/.resx files (whole-file
/// churn in git, "file completely changed" hooks) and silently transcoded UTF-16 files
/// (pre-1.6.0 architecture review, §1.9 — measured on four call sites). This helper detects the existing
/// file's BOM before writing and re-writes with the same encoding; a <em>new</em> file gets
/// UTF-8 without BOM, the modern default. Detection is BOM-based only: a BOM-less file is
/// treated as UTF-8, exactly what the read path (<c>File.ReadAllTextAsync</c>) already assumes,
/// so this can never regress a file the tools could read correctly in the first place.
/// </remarks>
internal static class SafeFileWriter
{
    internal static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The encoding an existing file's BOM declares — UTF-8 without BOM when it has none.</summary>
    internal static Encoding DetectEncoding(string path)
    {
        Span<byte> bom = stackalloc byte[4];
        int read;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            read = fs.Read(bom);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Diagnostics.Swallow($"SafeFileWriter.DetectEncoding({path})", ex);
            return Utf8NoBom;   // unreadable now → the write itself will surface the real error
        }

        // Order matters: UTF-32 LE starts with the UTF-16 LE BOM.
        if (read >= 4 && bom[0] == 0xFF && bom[1] == 0xFE && bom[2] == 0x00 && bom[3] == 0x00) return Encoding.UTF32;
        if (read >= 4 && bom[0] == 0x00 && bom[1] == 0x00 && bom[2] == 0xFE && bom[3] == 0xFF)
            return new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return Encoding.UTF8;             // UTF-8 with BOM
        if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode;                             // UTF-16 LE
        if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) return Encoding.BigEndianUnicode;                    // UTF-16 BE
        return Utf8NoBom;
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/>, keeping the encoding (and BOM)
    /// the existing file carries; a new file is written UTF-8 without BOM.
    /// </summary>
    internal static Task WritePreservingAsync(string path, string content, CancellationToken ct)
    {
        var encoding = File.Exists(path) ? DetectEncoding(path) : Utf8NoBom;
        return File.WriteAllTextAsync(path, content, encoding, ct);
    }
}
