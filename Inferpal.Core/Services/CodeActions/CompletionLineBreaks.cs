namespace Inferpal.Services.CodeActions;

/// <summary>
/// The line breaks of a completion inserted in a document: the document's, never the model's.
/// </summary>
/// <remarks>
/// ⚠ Shared in source with the in-process net472 assembly (ghost text): no API a .NET Framework string lacks.
/// The model writes LF; inserted as is in a CRLF document — Visual Studio's default on Windows —, an accepted ghost text
/// left a file of mixed line endings, which the next diff shows changed line by line.
/// </remarks>
internal static class CompletionLineBreaks
{
    /// <summary><paramref name="completion"/> with every line break written as <paramref name="documentBreak"/>.</summary>
    public static string In(string completion, string documentBreak)
    {
        if (completion.IndexOf('\n') < 0) return completion;
        var lf = completion.Replace("\r\n", "\n");
        return documentBreak == "\n" ? lf : lf.Replace("\n", documentBreak);
    }
}
