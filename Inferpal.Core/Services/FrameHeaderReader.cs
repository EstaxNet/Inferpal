using System.Text;

namespace Inferpal.Services;

/// <summary>
/// The header block of one <c>Content-Length</c> frame, read a byte at a time. The one reader of
/// this framing for the FIM sidecar's two ends and for the language-server transport.
/// </summary>
/// <remarks>
/// <para>
/// One reader, because private copies drift: a BOM looked for as U+FEFF on a line built by casting
/// bytes to chars — where it arrives as three chars — never matches, and the channel closes on the
/// first frame; and a header line read without a bound grows forever on a stream that never sends a
/// newline.
/// </para>
/// <para>
/// Compiled into the in-process assembly as well (net472, by source link): BCL only, and no C# 8
/// ranges or indices.
/// </para>
/// </remarks>
internal sealed class FrameHeaderReader
{
    /// <summary>Ceiling on a body: anything larger is not a message of these protocols.</summary>
    internal const int MaxBodyBytes = 8 * 1024 * 1024;

    /// <summary>A header line longer than this is not a header line.</summary>
    internal const int MaxLineChars = 4 * 1024;

    internal enum Outcome
    {
        /// <summary>The header block continues.</summary>
        Pending,
        /// <summary>The block ended with a usable length (<see cref="Length"/>).</summary>
        Complete,
        /// <summary>The block ended without a usable length: the sender and the reader no longer
        /// agree on where messages begin.</summary>
        NoLength,
        /// <summary>A line ran past <see cref="MaxLineChars"/> without ending.</summary>
        LineTooLong,
    }

    private const string Marker = "Content-Length:";

    // A UTF-8 BOM read byte by byte arrives as these three chars, never as U+FEFF.
    private const string BomAsChars = "\u00EF\u00BB\u00BF";

    private readonly StringBuilder _line = new StringBuilder(64);
    private bool _firstLine = true;

    /// <summary>The body length, once <see cref="Feed"/> returned <see cref="Outcome.Complete"/>.</summary>
    internal int Length { get; private set; } = -1;

    /// <summary>
    /// Whether a non-empty header line was read. A block that ends before any is a closed or idle
    /// stream; one that ends after some, without a length, is a sender this reader misunderstood —
    /// the two are not repaired in the same place.
    /// </summary>
    internal bool SawHeader { get; private set; }

    /// <summary>Feeds one byte of the header block.</summary>
    internal Outcome Feed(byte b)
    {
        if (b != (byte)'\n')
        {
            if (b == (byte)'\r') return Outcome.Pending;
            if (_line.Length >= MaxLineChars) return Outcome.LineTooLong;
            _line.Append((char)b);
            return Outcome.Pending;
        }

        var text = _line.ToString();
        _line.Length = 0;
        if (_firstLine && text.StartsWith(BomAsChars, StringComparison.Ordinal))
            text = text.Substring(BomAsChars.Length);
        _firstLine = false;

        if (text.Length == 0)
            return Length >= 0 ? Outcome.Complete : Outcome.NoLength;

        SawHeader = true;
        if (text.StartsWith(Marker, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(text.Substring(Marker.Length).Trim(), out var parsed)
            && parsed >= 0 && parsed <= MaxBodyBytes)
            Length = parsed;
        return Outcome.Pending;
    }
}
