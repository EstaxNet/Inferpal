using System.Text;

namespace Inferpal.Services.Inference;

/// <summary>
/// Watches a streamed text — a reasoning channel or an answer — for a model going round in circles: its most recent
/// passage already written several times in the text just before it.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <see cref="RepeatedCallDetector"/> only sees a loop made of tool calls, and <see cref="OutputBound"/> only ends a
/// loop once the response has used the room the window leaves — minutes of "Thinking…". A model can also loop on
/// plain text ("Let's try this set (3 sentences)…", word for word, over and over): nothing stopped it before the bound.
/// </para>
/// <para>
/// The test: the last <see cref="TailChars"/> characters occur at least <see cref="MinOccurrences"/> times in the last
/// <see cref="WindowChars"/>. Measured on real answers: 1 everywhere, 5 in a looping model's text. Four, not two,
/// so a model that quotes the same code twice while it thinks is left alone. Checked every
/// <see cref="CheckEveryChars"/> characters, on a bounded buffer: the cost does not grow with the response.
/// </para>
/// </remarks>
internal sealed class TextLoopDetector
{
    internal const int TailChars       = 200;
    internal const int WindowChars     = 4000;
    internal const int MinOccurrences  = 4;
    internal const int CheckEveryChars = 400;

    private readonly StringBuilder _recent = new();
    private int _sinceCheck;

    /// <summary>Whether the text, with <paramref name="delta"/> appended, now ends in a loop.</summary>
    public bool Repeats(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return false;
        _recent.Append(delta);
        if (_recent.Length > WindowChars) _recent.Remove(0, _recent.Length - WindowChars);

        _sinceCheck += delta.Length;
        if (_sinceCheck < CheckEveryChars || _recent.Length < TailChars * MinOccurrences) return false;
        _sinceCheck = 0;

        var text = _recent.ToString();
        var tail = text[^TailChars..];
        var count = 0;
        for (var i = text.IndexOf(tail, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(tail, i + 1, StringComparison.Ordinal))
            if (++count >= MinOccurrences) return true;
        return false;
    }
}
