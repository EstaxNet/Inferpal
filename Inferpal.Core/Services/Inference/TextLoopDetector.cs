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

    private readonly LoopWindow _window = new();

    /// <summary>Whether the text, with <paramref name="delta"/> appended, now ends in a loop.</summary>
    public bool Repeats(string delta)
    {
        if (_window.Sample(delta) is not { } text) return false;
        var tail = text[^TailChars..];
        var count = 0;
        for (var i = text.IndexOf(tail, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(tail, i + 1, StringComparison.Ordinal))
            if (++count >= MinOccurrences) return true;
        return false;
    }
}

/// <summary>
/// The passage both loop detectors look at: the last <see cref="TextLoopDetector.WindowChars"/> characters, offered
/// every <see cref="TextLoopDetector.CheckEveryChars"/> once long enough to hold the tail
/// <see cref="TextLoopDetector.MinOccurrences"/> times.
/// </summary>
internal sealed class LoopWindow
{
    private readonly StringBuilder _recent = new();
    private int _sinceCheck;

    /// <summary>The passage with <paramref name="delta"/> appended when it is time to look at it; <c>null</c> otherwise.</summary>
    public string? Sample(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return null;
        _recent.Append(delta);
        if (_recent.Length > TextLoopDetector.WindowChars) _recent.Remove(0, _recent.Length - TextLoopDetector.WindowChars);

        _sinceCheck += delta.Length;
        if (_sinceCheck < TextLoopDetector.CheckEveryChars
            || _recent.Length < TextLoopDetector.TailChars * TextLoopDetector.MinOccurrences) return null;
        _sinceCheck = 0;
        return _recent.ToString();
    }
}
