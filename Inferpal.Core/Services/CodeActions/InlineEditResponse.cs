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
    /// <summary>Removes surrounding markdown code fences and trailing whitespace from <paramref name="raw"/>.</summary>
    public static string Clean(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw ?? string.Empty;

        // Remove trailing whitespace and leading newlines (preserve leading spaces for indent detection).
        var s = raw.TrimEnd().TrimStart('\r', '\n');
        if (string.IsNullOrEmpty(s)) return s;

        // Split into lines; detect fences regardless of leading whitespace.
        var lines = s.Split('\n');
        var start = 0;
        var end   = lines.Length;

        // Opening fence: first line is optional-whitespace + ```...
        if (lines[0].TrimStart(' ', '\t').StartsWith("```", System.StringComparison.Ordinal))
            start = 1;

        // Closing fence: last line is optional-whitespace + ```
        if (end > start && lines[end - 1].TrimStart(' ', '\t').StartsWith("```", System.StringComparison.Ordinal))
            end--;

        if (start == 0 && end == lines.Length)
            return s; // No fence detected — return unchanged.

        // Reconstruct without fence lines; strip any extra blank lines introduced by the removal.
        var result = string.Join('\n', lines[start..end]);
        return result.TrimStart('\r', '\n').TrimEnd();
    }
}
