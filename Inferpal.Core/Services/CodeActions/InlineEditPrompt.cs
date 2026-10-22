namespace Inferpal.Services.CodeActions;

/// <summary>
/// "Edit with AI": a free instruction applied to a block of code — the prompt and the block both editors send, so the
/// same instruction on the same code asks the model the same thing in Visual Studio and in VS Code.
/// </summary>
internal static class InlineEditPrompt
{
    /// <summary>The system prompt of an inline edit.</summary>
    public const string System =
        "You are an expert code editor. The user provides a code block and an instruction. " +
        "Reply with ONLY the edited code — no explanation, no markdown fences, no ```.\n" +
        "CRITICAL: reproduce EXACTLY the same leading whitespace on EVERY line as the original. " +
        "ALL lines — including opening and closing braces — must keep their original indentation level. " +
        "Do NOT reset any line to column 0 if it was indented in the original. " +
        "Do not add or remove blank lines at the start or end.";

    /// <summary>The user message: the code, then the instruction — the order Visual Studio's command has always sent.</summary>
    public static string UserMessage(string code, string instruction) => $"Code to edit:\n{code}\n\nInstruction: {instruction.Trim()}";

    /// <summary>
    /// The block an edit applies to: the selection, its start moved back over the indentation of its first line; with
    /// nothing selected, the caret's line without its line break. <c>null</c> when that block holds only blanks — there
    /// is nothing to edit.
    /// </summary>
    /// <remarks>The first line's indentation goes with the block: without it the reindenter rebuilds every continuation
    /// line one level too shallow, while the first line, inserted at the caret's column, looks right.</remarks>
    public static (int Start, int End)? Target(string text, int selStart, int selEnd)
    {
        // Offsets from an editor: kept inside the text, so a stale pair is refused rather than thrown on.
        (selStart, selEnd) = (Math.Clamp(selStart, 0, text.Length), Math.Clamp(selEnd, 0, text.Length));
        int start, end;
        if (selEnd > selStart)
        {
            // The code actions' own rule (CodeActionPipeline.ResolveTarget): one place keeps the reindenter right.
            (_, start, end, _) = CodeActionPipeline.ResolveTarget(text, selStart, selEnd, selectionEmpty: false);
        }
        else
        {
            var ls = selStart > 0 ? text.LastIndexOf('\n', selStart - 1) : -1;
            start = ls < 0 ? 0 : ls + 1;
            var le = text.IndexOf('\n', selStart);
            var rawEnd = le < 0 ? text.Length : le;
            end = rawEnd > start && text[rawEnd - 1] == '\r' ? rawEnd - 1 : rawEnd;
        }
        return string.IsNullOrWhiteSpace(text[start..end]) ? null : (start, end);
    }
}
