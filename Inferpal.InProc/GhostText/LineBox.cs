using Microsoft.VisualStudio.Text.Formatting;

namespace Inferpal.GhostText;

/// <summary>
/// Where an editor line's text is: the only vertical geometry a row drawn for that line may use.
/// </summary>
/// <remarks>
/// ⚠ A line's box (<c>Top</c>, <c>Height</c>, <c>Bottom</c>) includes the space another adornment asks for above its
/// text — CodeLens's "N references" over a declaration, for one. Drawn from the box, a suggestion lands on that space, one
/// row above the text it belongs to. So a row starts at the text, and the next one is a text height lower.
/// </remarks>
internal readonly record struct LineBox(double TextTop, double TextHeight)
{
    /// <summary>The top of the row <paramref name="index"/> drawn from this line: the first at its text, each next one a
    /// text height lower.</summary>
    internal double RowTop(int index) => TextTop + index * TextHeight;

    /// <summary>The bottom of the line's text.</summary>
    internal double TextBottom => TextTop + TextHeight;
}

/// <summary>The <see cref="LineBox"/> of what the editor reports.</summary>
/// <remarks>Apart from <see cref="LineBox"/>: the tests reach <see cref="LineBox"/> without the editor's assemblies.</remarks>
internal static class LineBoxes
{
    /// <summary>Where the text of the line holding a character is.</summary>
    internal static LineBox Of(TextBounds b) => new(TextTop: b.TextTop, TextHeight: b.TextHeight);

    /// <summary>Where the text of a formatted line is.</summary>
    internal static LineBox Of(ITextViewLine line) => new(TextTop: line.TextTop, TextHeight: line.TextHeight);
}
