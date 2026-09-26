namespace Inferpal.Services.CodeActions;

/// <summary>
/// What an inline (FIM) completion inserts, once the text already after the caret is taken into account.
/// </summary>
/// <remarks>
/// ⚠ Models repeat what follows the caret — a FIM model that is GIVEN the suffix as much as the prefix-only
/// fallback: inside <c>Console.WriteLine(|);</c> the completion ends with its own <c>);</c>, and accepted it leaves
/// <c>…));</c>; <c>string name = "|";</c> becomes <c>"John Doe";";</c>. Every mid-line suggestion then needs fixing
/// by hand, in both editors.
/// ⚠ The rule is the one that cannot unbalance a line: only the WHOLE rest of the line is removed from the end of
/// the completion, never a single closer that happens to match — <c>WriteLine(|)</c> completed with
/// <c>Math.Abs(x)</c> keeps its parenthesis. Mid-line, a completion is one line: the editor's line goes on after it.
/// </remarks>
internal static class FimCompletion
{
    /// <summary>The text to insert at the caret, given the <paramref name="suffix"/> that follows it.</summary>
    public static string Finish(string completion, string suffix)
    {
        if (string.IsNullOrEmpty(completion)) return completion;

        var newline    = suffix.IndexOf('\n');
        var restOfLine = (newline < 0 ? suffix : suffix[..newline]).TrimEnd('\r');

        if (restOfLine.Trim().Length > 0)
        {
            var cut  = completion.IndexOf('\n');
            var line = (cut < 0 ? completion : completion[..cut]).TrimEnd('\r');
            if (line.EndsWith(restOfLine, StringComparison.Ordinal))
                return line[..^restOfLine.Length];
            var closing = restOfLine.Trim();
            var ending  = line.TrimEnd();
            return ending.EndsWith(closing, StringComparison.Ordinal)
                ? ending[..^closing.Length].TrimEnd()
                : line;
        }

        // At the end of a line: drop the completion's last lines when they repeat, line for line, the lines that
        // follow the caret — typically the closing braces of the block the caret is in.
        var following = (newline < 0 ? [] : suffix[(newline + 1)..].Split('\n'))
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var lines = completion.Split('\n').ToList();
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);

        for (var k = Math.Min(lines.Count, following.Count); k > 0; k--)
        {
            var tail = lines.Skip(lines.Count - k).Select(l => l.Trim()).ToList();
            if (tail.SequenceEqual(following.Take(k), StringComparer.Ordinal))
                return string.Join('\n', lines.Take(lines.Count - k)).TrimEnd();
        }
        return completion;
    }
}
