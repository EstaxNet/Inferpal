using System.Text;

namespace Inferpal.Services.Inference;

/// <summary>
/// Watches the streamed arguments of a structured tool call for a model going round in circles: the same JSON written
/// again and again.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ The third channel a model loops in. <see cref="TextLoopDetector"/> watches the reasoning and the answer; the
/// arguments of a structured call are counted by <see cref="OutputBound"/> and by nothing else, and no front-end shows
/// them — without this, minutes of "Thinking…" before the bound. Devstral, writing an <c>apply_edits</c> whose code
/// holds a C# interpolated string (<c>$"…"</c>), breaks its JSON there and then repeats the same edit object, or opens
/// new calls inside the arguments, until something stops it.
/// </para>
/// <para>
/// ⚠ The text loop test alone is not the signal: arguments carry FILES, and a file repeats itself (separator lines,
/// identical test bodies), and a list of edits on one file repeats its path and keys. Two facts set a model's loop
/// apart, both required:
/// <list type="bullet">
///   <item><description>the repeating block contains a raw quote — JSON STRUCTURE repeating. A file is written inside
///     one string value, where every quote is escaped (<c>\"</c>), however much the file repeats;</description></item>
///   <item><description>the block repeats EXACTLY, three times in a row. Similar edits differ somewhere in each one (a
///     name, a line); a loop does not.</description></item>
/// </list>
/// </para>
/// </remarks>
internal sealed class ArgumentsLoopDetector
{
    internal const int ExactRepeats = 3;

    private readonly StringBuilder _recent = new();
    private int _sinceCheck;

    /// <summary>Whether the arguments, with <paramref name="delta"/> appended, now repeat their own structure.</summary>
    public bool Repeats(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return false;
        _recent.Append(delta);
        if (_recent.Length > TextLoopDetector.WindowChars) _recent.Remove(0, _recent.Length - TextLoopDetector.WindowChars);

        _sinceCheck += delta.Length;
        if (_sinceCheck < TextLoopDetector.CheckEveryChars
            || _recent.Length < TextLoopDetector.TailChars * TextLoopDetector.MinOccurrences) return false;
        _sinceCheck = 0;

        var text = _recent.ToString();
        var tail = text[^TextLoopDetector.TailChars..];
        int count = 0, last = -1, before = -1;
        for (var i = text.IndexOf(tail, StringComparison.Ordinal); i >= 0; i = text.IndexOf(tail, i + 1, StringComparison.Ordinal))
        {
            count++;
            before = last;
            last = i;
        }
        if (count < TextLoopDetector.MinOccurrences) return false;

        var period = last - before;
        if (ExactRepeats * period > text.Length) return false;
        var block = text.AsSpan(text.Length - period);
        for (var k = 2; k <= ExactRepeats; k++)
            if (!text.AsSpan(text.Length - k * period, period).SequenceEqual(block)) return false;
        return HasRawQuote(block);
    }

    /// <summary>A quote that is not escaped: preceded by an even number of backslashes.</summary>
    private static bool HasRawQuote(ReadOnlySpan<char> s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '"') continue;
            var backslashes = 0;
            for (var j = i - 1; j >= 0 && s[j] == '\\'; j--) backslashes++;
            if (backslashes % 2 == 0) return true;
        }
        return false;
    }
}
