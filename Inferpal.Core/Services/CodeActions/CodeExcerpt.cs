namespace Inferpal.Services.CodeActions;

/// <summary>
/// The code a read-only code action sends (<c>/explain</c>, <c>/review</c>, the editor's context-menu
/// commands), cut to a budget — and the cut SAID, to both readers.
/// </summary>
/// <remarks>
/// ⚠ The budget exists to fit the default context window; the defect was never the cap but its
/// silence. <c>/review</c> answers with a VERDICT, and a third of the source files of an ordinary
/// repository exceed the budget: the model read a bare <c>…(truncated)</c> that says nothing of how
/// much is missing, and the human saw the file's name under their question and a review of the whole
/// file — its first two hundred lines. So the marker names the count (model), and so does the label
/// the chat shows under the question (<see cref="Label"/>, human).
/// </remarks>
/// <param name="Text">What is sent: the code, or its first lines followed by the marker.</param>
/// <param name="ShownLines">Lines sent.</param>
/// <param name="TotalLines">Lines the code has.</param>
internal readonly record struct CodeExcerpt(string Text, int ShownLines, int TotalLines)
{
    /// <summary>Budget of one excerpt, in characters.</summary>
    public const int MaxChars = 8_000;

    public bool IsTruncated => ShownLines < TotalLines;

    public static CodeExcerpt Of(string code, int maxChars = MaxChars)
    {
        var total = CountLines(code);
        if (code.Length <= maxChars) return new(code, total, total);

        // Cut on a line end, so "the first N lines" is exactly what was sent. A first line longer than
        // the whole budget (minified code) has no line end to cut on: it goes out cut, and counts as shown.
        var end  = code.LastIndexOf('\n', maxChars - 1);
        var head = end > 0 ? code[..end] : SafeTruncate.Truncate(code, maxChars);
        var shown = CountLines(head);

        // Model-facing and structural, like the bare marker it replaces: not localized.
        return new(
            head + $"\n…(truncated: the first {shown} of {total} lines are shown — the rest was not sent)",
            shown, total);
    }

    /// <summary>
    /// The chip label under the question: unchanged for a whole excerpt, carrying the count otherwise.
    /// </summary>
    public string Label(string label) =>
        IsTruncated ? Localization.Strings.CodeExcerptLabel(label, ShownLines, TotalLines) : label;

    private static int CountLines(string s)
    {
        if (s.Length == 0) return 0;
        var n = 1;
        foreach (var c in s) if (c == '\n') n++;
        return s[^1] == '\n' ? n - 1 : n;
    }
}
