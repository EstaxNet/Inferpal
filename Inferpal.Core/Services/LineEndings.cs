namespace Inferpal.Services;

/// <summary>
/// Line endings of a text the model rewrites. Model output is LF; a CRLF file — Visual Studio's
/// default on Windows — must get its own endings back, or the file ends up with mixed endings.
/// </summary>
internal static class LineEndings
{
    /// <summary><c>"\r\n"</c> when most of <paramref name="text"/>'s line breaks are CRLF, else <c>"\n"</c>.</summary>
    public static string Dominant(string text)
    {
        int lf = 0, crlf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            lf++;
            if (i > 0 && text[i - 1] == '\r') crlf++;
        }
        return crlf * 2 > lf ? "\r\n" : "\n";
    }

    /// <summary><paramref name="text"/> with every CRLF or LF break written as <paramref name="eol"/>.</summary>
    public static string ToEol(string text, string eol)
    {
        var lf = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return eol == "\n" ? lf : lf.Replace("\n", eol, StringComparison.Ordinal);
    }
}
