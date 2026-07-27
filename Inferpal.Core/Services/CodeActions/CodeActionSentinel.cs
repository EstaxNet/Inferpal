namespace Inferpal.Services.CodeActions;

/// <summary>
/// Shared escape hatch for the in-place code actions (Refactor / Fix / Add-docs / Test): instead
/// of being forced to always emit code, the model is told (in the system prompts) to reply with
/// EXACTLY <see cref="Token"/> when the requested change would bring nothing — the code is already
/// clear, correct and documented, or every meaningful case is already covered by tests.
/// The pipelines detect it and report "nothing to do" rather than applying a no-op edit.
/// <para>Pure and testable — no editor dependency.</para>
/// </summary>
internal static class CodeActionSentinel
{
    /// <summary>
    /// Token the model emits when the action is a no-op. Deliberately unlikely to appear verbatim
    /// in real source so it can never be mistaken for legitimate output.
    /// </summary>
    public const string Token = "INFERPAL_NO_CHANGE_NEEDED";

    /// <summary>
    /// True when <paramref name="cleaned"/> (already stripped of markdown fences by
    /// <see cref="InlineEditResponse.Clean"/>) is just the sentinel. Tolerates surrounding
    /// whitespace, quotes/backticks and trailing punctuation that small models sometimes add,
    /// but stays tight: only a reply that is *essentially nothing but* the token counts, so a real
    /// file that merely mentions the token (e.g. in a comment) is never treated as a no-op.
    /// </summary>
    public static bool IsNoChange(string? cleaned)
    {
        if (string.IsNullOrWhiteSpace(cleaned)) return false;

        var trimmed = cleaned.Trim().Trim('`', '"', '\'', '.', '!', ' ', '\r', '\n', '\t');
        return trimmed.Equals(Token, System.StringComparison.OrdinalIgnoreCase);
    }
}
