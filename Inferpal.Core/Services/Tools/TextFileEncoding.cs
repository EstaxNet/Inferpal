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
            // With the provider registered, code page 0 is the Windows ANSI code page (GetACP); elsewhere there is none.
            var system   = OperatingSystem.IsWindows() ? Encoding.GetEncoding(0).CodePage : 0;
            var codePage = LegacyCodePage(system, System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
            if (codePage > 0) return Encoding.GetEncoding(codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            Diagnostics.Swallow("TextFileEncoding.LegacyEncoding", ex);
        }
        return Encoding.Latin1;
    }

    /// <summary>
    /// The code page a file with no BOM that is not UTF-8 was most likely saved in; 0 when there is no guess.
    /// </summary>
    /// <remarks>
    /// ⚠ On Windows it is the SYSTEM ANSI code page — "language for non-Unicode programs", what Visual Studio and every
    /// legacy editor saved with — not the regional FORMAT (<c>CurrentCulture</c>): a Chinese system with English formats
    /// saved GBK, and its culture says 1252. The culture's code page is the guess left when the system one is UTF-8
    /// (Windows' "worldwide language support" option) or does not exist (Linux, macOS).
    /// </remarks>
    internal static int LegacyCodePage(int systemCodePage, int cultureCodePage) =>
        systemCodePage is > 0 and not 65001 ? systemCodePage
        : cultureCodePage is > 0 and not 65001 ? cultureCodePage
        : 0;

    /// <summary>
    /// The first character of <paramref name="text"/> that <paramref name="encoding"/> cannot hold, with its 1-based
    /// line; <c>null</c> when it holds them all — always, for a Unicode encoding.
    /// </summary>
    /// <remarks>
    /// ⚠ Encoding it anyway does not fail: the code page's fallback writes "?" — or a look-alike, Polish "zażółć"
    /// becoming "zazólc" in Windows-1252 — silently, behind an approval prompt that showed the real text.
    /// </remarks>
    internal static (string Character, int Line)? FirstUnrepresentable(Encoding encoding, string text)
    {
        if (encoding.CodePage is 65001 or 1200 or 1201 or 12000 or 12001) return null;
        var strict = Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
        try
        {
            strict.GetByteCount(text);
            return null;
        }
        catch (EncoderFallbackException ex)
        {
            var character = ex.CharUnknownHigh != '\0'
                ? new string([ex.CharUnknownHigh, ex.CharUnknownLow])
                : ex.CharUnknown.ToString();
            var line = 1 + text.AsSpan(0, Math.Clamp(ex.Index, 0, text.Length)).Count('\n');
            return (character, line);
        }
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
    /// <c>true</c> when <paramref name="bytes"/> are not text: a NUL in the first 8 000 bytes, as git judges it — unless
    /// a UTF-16/32 byte order mark says the NULs are half of every character.
    /// </summary>
    /// <remarks>⚠ Read as text, a .dll reached the model as 7 676 characters of which 2 426 were NULs — noise read as
    /// content — and a search listed lines of it as "matches".</remarks>
    internal static bool IsBinary(ReadOnlySpan<byte> bytes)
    {
        if (bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..] or [0x00, 0x00, 0xFE, 0xFF, ..]) return false;
        return bytes[..Math.Min(bytes.Length, BinarySniffBytes)].Contains((byte)0);
    }

    /// <summary><see cref="IsBinary"/> of a file's head; a file that cannot be read is judged by the read that follows.</summary>
    internal static bool IsBinaryFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var head = new byte[Math.Min(fs.Length, BinarySniffBytes)];
            fs.ReadExactly(head);
            return IsBinary(head);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private const int BinarySniffBytes = 8000;

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

    /// <summary>
    /// Text in which each LINE may be in its own encoding: UTF-8 when the line's bytes are UTF-8, the legacy code page
    /// otherwise.
    /// </summary>
    /// <remarks>
    /// ⚠ For output that quotes files: a git diff prints each file's content as the bytes it holds, between header
    /// lines in UTF-8 — one decoding for the whole output turns either half to mojibake. A line is the unit because
    /// 0x0A never occurs inside a character of an ASCII-compatible code page, the double-byte ones (GBK, Shift_JIS)
    /// included — a split by byte would cut their characters in two.
    /// </remarks>
    internal static string DecodeLines(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder(bytes.Length);
        while (!bytes.IsEmpty)
        {
            var end  = bytes.IndexOf((byte)'\n');
            var line = end < 0 ? bytes : bytes[..(end + 1)];
            text.Append((System.Text.Unicode.Utf8.IsValid(line) ? Utf8NoBom : LegacyEncoding).GetString(line));
            bytes = bytes[line.Length..];
        }
        return text.ToString();
    }

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
