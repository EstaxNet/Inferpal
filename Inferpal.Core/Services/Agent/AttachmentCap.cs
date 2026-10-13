using System.Globalization;

namespace Inferpal.Services.Agent;

/// <summary>
/// What an attachment sends of a file — and, when that is not the whole file, says so to both readers: the model reads
/// the counts in the marker, the person reads them under the question (the chip's label). A cut that nobody names is a
/// file that ends early: "is X defined in this file?" is then answered "no" about code that sits past the cut. Uncut,
/// a whole file outgrows the window: LM Studio refuses the request, Ollama drops its head, system prompt included.
/// </summary>
/// <remarks>VS Code's chips apply the same rule and the same figure (<c>vscode/src/attachmentCap.ts</c>).</remarks>
internal static class AttachmentCap
{
    /// <summary>Characters an attachment sends at most.</summary>
    public const int MaxChars = 60_000;

    /// <param name="Text">What is sent: the text, or its first lines followed by the marker.</param>
    /// <param name="Shown">Characters of the original that are sent.</param>
    /// <param name="Total">Characters of the original.</param>
    public readonly record struct Capped(string Text, int Shown, int Total, bool Cut);

    /// <summary>The text within <paramref name="max"/> characters, cut on a line end when one is near, the cut said with
    /// both counts.</summary>
    public static Capped Of(string text, int max = MaxChars)
    {
        if (text.Length <= max) return new Capped(text, text.Length, text.Length, Cut: false);
        var lineEnd = text.LastIndexOf('\n', max);
        var shown   = lineEnd > max / 2 ? lineEnd : max;
        // For the model, in English like every structural marker; it names the tool that reads the rest.
        var marker = $"\n…(truncated: the first {shown.ToString("N0", CultureInfo.InvariantCulture)} of "
                   + $"{text.Length.ToString("N0", CultureInfo.InvariantCulture)} characters are attached; read_file reads the rest)";
        return new Capped(text[..shown] + marker, shown, text.Length, Cut: true);
    }
}
