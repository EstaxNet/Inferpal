namespace Inferpal.Services.CodeActions;

/// <summary>
/// Pure, testable cleanup of a model's reply for in-place code edits
/// ("Edit with AI" + the Refactor / Fix / Add-docs context-menu actions).
/// <para>
/// Strips a leading <c>```lang</c> / <c>```</c> fence and a trailing <c>```</c> from the
/// output. Handles fences that carry leading whitespace (models sometimes indent their
/// fences when the original code is indented, e.g. <c>    ```csharp</c>).
/// </para>
/// <para>
/// Only leading <b>newlines</b> (not spaces) are stripped from the result so that
/// <see cref="InlineEditReindenter"/> can still detect the base-indentation delta.
/// </para>
/// </summary>
internal static class InlineEditResponse
{
    /// <summary>Removes the model's leading reasoning, surrounding markdown code fences and trailing whitespace
    /// from <paramref name="raw"/>.</summary>
    /// <remarks>A server that does not separate reasoning sends it at the head of the reply: left there, it is
    /// written into the file as code (<see cref="MarkdownParser.WithoutLeadingReasoning"/>).</remarks>
    /// <param name="raw">The model's reply.</param>
    /// <param name="ownFences">The text being rewritten carries fences of its own (<see cref="HasOwnFences"/>): only a
    /// pair wrapping the whole reply goes. Otherwise a fence line anywhere is the model's wrapping.</param>
    public static string Clean(string raw, bool ownFences = false)
    {
        raw = MarkdownParser.WithoutLeadingReasoning(raw);
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        // Remove trailing whitespace and leading newlines (preserve leading spaces for indent detection).
        var s = raw.TrimEnd().TrimStart('\r', '\n');
        if (string.IsNullOrEmpty(s)) return s;

        // Split into lines; detect fences regardless of leading whitespace.
        var lines = s.Split('\n');

        // ⚠ Prose around the block ("Here is the fix:" before, "This version…" after) is not code: kept, the sentence and
        // the fence lines land in the file. In code that has no fence of its own, the first block is the answer.
        if (!ownFences)
        {
            var open = Array.FindIndex(lines, IsFence);
            if (open < 0) return s;   // No fence detected — return unchanged.
            var close = Array.FindIndex(lines, open + 1, IsFence);
            // A lone fence on the last line closes the code above it; any other opens the code below it.
            var body  = close < 0 && open == lines.Length - 1 ? lines[..open] : lines[(open + 1)..(close < 0 ? lines.Length : close)];
            return string.Join('\n', body).TrimStart('\r', '\n').TrimEnd();
        }

        // Code with fences of its own (Markdown): only a wrapping PAIR goes — an opening first line, then the closing last
        // one. A last fence alone is the content's own block closing.
        if (!IsFence(lines[0])) return s;
        var end = lines.Length > 1 && IsFence(lines[^1]) ? lines.Length - 1 : lines.Length;
        return string.Join('\n', lines[1..end]).TrimStart('\r', '\n').TrimEnd();
    }

    /// <summary>
    /// Whether the text a reply rewrites carries fences of its own: a documentation file (Markdown and its kin, plain
    /// text) — judged by the FILE, since a selected paragraph of a README has none and its rewrite may add one — or a
    /// document that already holds a fence line.
    /// </summary>
    internal static bool HasOwnFences(string? path, string document) =>
        (path is not null && DocumentationExtensions.Contains(System.IO.Path.GetExtension(path)))
        || document.Split('\n').Any(IsFence);

    private static readonly HashSet<string> DocumentationExtensions = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".mdx", ".txt", ".rst", ".adoc",
    };

    private static bool IsFence(string line) => line.TrimStart(' ', '\t').StartsWith("```", System.StringComparison.Ordinal);
}
