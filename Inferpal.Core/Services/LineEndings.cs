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

    /// <summary>
    /// <paramref name="rewrite"/> ending on a line break exactly when <paramref name="original"/> does — the break in
    /// the original's own convention.
    /// </summary>
    /// <remarks>
    /// ⚠ A model rewriting a whole file often drops the final line break: the diff then shows the last line changed
    /// ("\ No newline at end of file") though it was not, and an "undo" that rewrites the file by hand is off by
    /// that one byte. The final break is a convention of the file, like its line endings — never the model's decision.
    /// An empty rewrite stays empty, and an original without any line break says nothing: the rewrite is kept.
    /// </remarks>
    public static string WithFinalBreakOf(string original, string rewrite)
    {
        if (rewrite.Length == 0 || !original.Contains('\n')) return rewrite;
        var had = original.EndsWith('\n');
        var has = rewrite.EndsWith('\n');
        if (had && !has) return rewrite + (original.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n");
        if (!had && has)
            return rewrite[..^(rewrite.EndsWith("\r\n", StringComparison.Ordinal) ? 2 : 1)];
        return rewrite;
    }
}
