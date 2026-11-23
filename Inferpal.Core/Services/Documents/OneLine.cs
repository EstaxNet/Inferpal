using System.Globalization;

namespace Inferpal.Services.Documents;

/// <summary>
/// Text a file holds, written on one line of a reader's summary — the one rule of every reader: nothing the file holds writes
/// a line of its own, and a credential is masked before its text is cut.
/// </summary>
/// <remarks>⚠ A line breaks on more than CR and LF: U+2028, U+2029, NEL (U+0085), a vertical tab or a form feed end a line
/// for some readers of the text. Every whitespace run becomes one space, every other control character a space.</remarks>
internal static class OneLine
{
    /// <summary><paramref name="text"/> on one line, trimmed.</summary>
    internal static string Flat(string? text)
    {
        var spaced = string.Create((text ?? string.Empty).Length, text ?? string.Empty, (span, s) =>
        {
            for (var i = 0; i < s.Length; i++) span[i] = char.IsControl(s[i]) ? ' ' : s[i];
        });
        return string.Join(' ', spaced.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary><paramref name="text"/> with every character that breaks a line or controls the terminal made a space, and
    /// nothing else touched: for a reader whose spacing means something (two spaces between a selector and its snippet).</summary>
    internal static string Unbroken(string text) => string.Create(text.Length, text, (span, s) =>
    {
        for (var i = 0; i < s.Length; i++)
            span[i] = char.IsControl(s[i]) || s[i] is '\u2028' or '\u2029' ? ' ' : s[i];
    });

    /// <summary>The widest slice masked before a cut: a credential cut short is no longer one the redactor recognises.</summary>
    internal const int MaskedSliceChars = 4096;

    /// <summary>The start of <paramref name="text"/> on one line, what looks like a credential masked (best effort), cut to
    /// <paramref name="max"/> characters and its length said when it is cut. Masked on a slice of
    /// <see cref="MaskedSliceChars"/> BEFORE the cut; a body of megabytes is never split whole.</summary>
    internal static string Masked(string text, int max)
    {
        var masked = SecretRedactor.Redact(text.Length > MaskedSliceChars ? text[..MaskedSliceChars] : text);
        var line = Flat(masked);
        return line.Length <= max && text.Length <= MaskedSliceChars ? line
             : line[..Math.Min(max, line.Length)] + string.Create(CultureInfo.InvariantCulture, $"… ({text.Length} characters)");
    }
}
