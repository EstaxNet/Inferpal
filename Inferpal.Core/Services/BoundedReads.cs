using System.IO;
using System.Net.Http;
using System.Text;

namespace Inferpal.Services;

/// <summary>
/// Reads of what a remote server sends, each under a ceiling.
/// </summary>
/// <remarks>
/// ⚠ <c>HttpClient.MaxResponseContentBufferSize</c> bounds only the body <c>HttpClient</c> buffers itself: a response
/// asked with <c>ResponseHeadersRead</c>, then read with <c>ReadAsStringAsync</c> or <c>ReadAsByteArrayAsync</c>, has no
/// limit at all, and a server that answers gigabytes fills the memory of the process the chat runs in. So does
/// <c>ReadLineAsync</c> on a line that never ends (<see cref="BoundedLineReader"/>).
/// </remarks>
internal static class BoundedReads
{
    /// <summary>The body as bytes, refused past <paramref name="maxBytes"/> (its declared length or what was read).</summary>
    internal static async Task<byte[]> BytesAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength is long declared && declared > maxBytes) throw TooLarge(maxBytes);
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw TooLarge(maxBytes);
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>The body as text in its declared charset (UTF-8 when it declares none), refused past
    /// <paramref name="maxBytes"/>; a byte-order mark is read as the encoding it names, never as text.</summary>
    internal static async Task<string> StringAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        var bytes = await BytesAsync(content, maxBytes, ct).ConfigureAwait(false);
        Encoding encoding;
        try { encoding = content.Headers.ContentType?.CharSet is { Length: > 0 } cs ? Encoding.GetEncoding(cs.Trim('"')) : Encoding.UTF8; }
        catch (ArgumentException) { encoding = Encoding.UTF8; }
        // ⚠ A BOM kept as text is a U+FEFF before the first "{": the JSON parser refuses the whole body.
        using var reader = new StreamReader(new MemoryStream(bytes), encoding, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    private static HttpRequestException TooLarge(int maxBytes) =>
        new($"the response is larger than {maxBytes / (1024 * 1024)} MB and was not read");
}

/// <summary>
/// The lines of <paramref name="reader"/>, each under <paramref name="maxChars"/>: a longer one is skipped to its end
/// without being kept. A line ends at <c>\n</c>, <c>\r\n</c> or a lone <c>\r</c> — the three endings an event stream may
/// use.
/// </summary>
internal sealed class BoundedLineReader(TextReader reader, int maxChars)
{
    private readonly char[] _buffer = new char[8192];
    private int _pos, _len;
    private bool _skipLf;   // a line just ended on '\r': a '\n' right after belongs to that ending

    /// <summary>The next line — <c>null</c> at the end of the stream — and whether it ran past the ceiling (then it comes
    /// back empty).</summary>
    public async Task<(string? Line, bool Overlong)> ReadLineAsync(CancellationToken ct)
    {
        var line = new StringBuilder();
        var overlong = false;
        var any = false;
        while (true)
        {
            if (_pos == _len)
            {
                _len = await reader.ReadAsync(_buffer.AsMemory(), ct).ConfigureAwait(false);
                _pos = 0;
                if (_len == 0) return any ? (overlong ? string.Empty : line.ToString(), overlong) : (null, false);
            }
            if (_skipLf)
            {
                _skipLf = false;
                if (_buffer[_pos] == '\n') { _pos++; continue; }
            }
            any = true;
            var start = _pos;
            while (_pos < _len && _buffer[_pos] is not ('\n' or '\r')) _pos++;
            if (!overlong)
            {
                if (line.Length + (_pos - start) > maxChars) { overlong = true; line.Clear(); }
                else line.Append(_buffer, start, _pos - start);
            }
            if (_pos < _len)
            {
                _skipLf = _buffer[_pos] == '\r';
                _pos++;
                return (overlong ? string.Empty : line.ToString(), overlong);
            }
        }
    }
}
