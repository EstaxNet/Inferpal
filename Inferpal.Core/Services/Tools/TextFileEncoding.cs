using System.IO;
using System.Text;

namespace Inferpal.Services.Tools;

/// <summary>
/// The encoding a text file is in, and the one way to read it that an edit can write back unchanged
/// (<see cref="SafeFileWriter"/> writes in the encoding detected here).
/// </summary>
/// <remarks>
/// ⚠ A BOM-less file is UTF-8 only when its bytes ARE UTF-8. Otherwise it is in the machine's legacy
/// code page (<see cref="LegacyEncoding"/>) — a Windows-1252 source with accented comments, what
/// Visual Studio saved on a Western machine. Read as UTF-8, every accent became U+FFFD, and the tools
/// that rewrite the whole file wrote "�" over lines the edit never touched — behind an approval prompt
/// that compares two already-decoded texts, so it showed none of it. The read that feeds an edit, and
/// the read the model quotes from (<c>read_file</c>), go through <see cref="ReadTextAsync"/> for the
/// same reason: an accent shown as "�" can never be quoted back into an <c>old_content</c>.
/// ⚠ Reading only: a file that references <see cref="SafeFileWriter"/> is taken for a file that writes
/// (convention rule "mutating tools ask"), which is right, so <c>read_file</c> must not have to.
/// </remarks>
internal static class TextFileEncoding
{
    internal static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly Lazy<Encoding> _legacy = new(ResolveLegacyEncoding);

    /// <summary>
    /// The encoding of a BOM-less file whose bytes are not UTF-8: the machine's legacy ("ANSI") code page, else
    /// Latin-1, which at least gives every byte back unchanged.
    /// </summary>
    internal static Encoding LegacyEncoding => _legacy.Value;

    private static Encoding ResolveLegacyEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var codePage = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            if (codePage > 0 && codePage != Encoding.UTF8.CodePage) return Encoding.GetEncoding(codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            Diagnostics.Swallow("TextFileEncoding.LegacyEncoding", ex);
        }
        return Encoding.Latin1;
    }

    /// <summary>The encoding an existing file is in: its BOM's, else UTF-8 when its bytes are UTF-8, else
    /// <see cref="LegacyEncoding"/>.</summary>
    internal static Encoding Detect(string path)
    {
        byte[] bytes;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Diagnostics.Swallow($"TextFileEncoding.Detect({path})", ex);
            return Utf8NoBom;   // unreadable now → the write itself will surface the real error
        }
        return Of(bytes);
    }

    /// <summary>The encoding <paramref name="bytes"/> are in — see <see cref="Detect"/>.</summary>
    internal static Encoding Of(ReadOnlySpan<byte> bytes)
    {
        // Order matters: UTF-32 LE starts with the UTF-16 LE BOM.
        if (bytes is [0xFF, 0xFE, 0x00, 0x00, ..]) return Encoding.UTF32;
        if (bytes is [0x00, 0x00, 0xFE, 0xFF, ..]) return new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        if (bytes is [0xEF, 0xBB, 0xBF, ..])       return Encoding.UTF8;               // UTF-8 with BOM
        if (bytes is [0xFF, 0xFE, ..])             return Encoding.Unicode;            // UTF-16 LE
        if (bytes is [0xFE, 0xFF, ..])             return Encoding.BigEndianUnicode;   // UTF-16 BE
        return System.Text.Unicode.Utf8.IsValid(bytes) ? Utf8NoBom : LegacyEncoding;
    }

    /// <summary>
    /// Reads a text file in the encoding <see cref="SafeFileWriter.WritePreservingAsync"/> will write it back in — the
    /// read every edit starts from, and the one the model quotes from.
    /// </summary>
    internal static async Task<string> ReadTextAsync(string path, CancellationToken ct) =>
        Decode(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));

    /// <summary><see cref="ReadTextAsync"/>, for the readers that are synchronous (a mention, a pinned file).</summary>
    /// <remarks>⚠ Every reader whose text the model may QUOTE into an edit goes through here: a line shown
    /// as "caf�" by one reader does not match the "café" another decoded, and the edit answers "not found".</remarks>
    internal static string ReadText(string path) => Decode(File.ReadAllBytes(path));

    /// <summary>The file's lines, split like <see cref="File.ReadAllLines(string)"/> (CR, LF or CRLF).</summary>
    internal static List<string> ReadLines(string path)
    {
        var lines = new List<string>();
        using var reader = new StringReader(ReadText(path));
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    private static string Decode(byte[] bytes)
    {
        var encoding = Of(bytes);
        var preamble = encoding.GetPreamble();
        var start    = preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble) ? preamble.Length : 0;
        return encoding.GetString(bytes, start, bytes.Length - start);
    }
}
