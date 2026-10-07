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
/// ⚠ And only what the completion closes IN EXCESS is a repetition: matching text is not enough. A balanced
/// <c>string.IsNullOrEmpty(name)</c> in <c>if (|)</c> keeps its <c>)</c>; a block written before a method's <c>}</c>
/// keeps its own <c>}</c> — cut, the method's brace closes the block and the file no longer compiles.
/// </remarks>
internal static class FimCompletion
{
    /// <summary>Whether the caret's line goes on after it: a completion there is ONE line (<see cref="Finish"/>).</summary>
    internal static bool IsMidLine(string suffix) => RestOfLine(suffix).Trim().Length > 0;

    private static string RestOfLine(string suffix)
    {
        var newline = suffix.IndexOf('\n');
        return (newline < 0 ? suffix : suffix[..newline]).TrimEnd('\r');
    }

    /// <summary>The stop sequences of a completion requested at a caret followed by <paramref name="suffix"/>.</summary>
    /// <remarks>
    /// ⚠ Mid-line, only the completion's first line is ever inserted. Without a line-break stop the model writes on —
    /// lines <see cref="Finish"/> throws away — and the suggestion waits for every one of them. Stopped at the first
    /// line break, the server returns exactly the line that would have been kept.
    /// </remarks>
    internal static string[] Stops(string suffix, string[] stops) =>
        IsMidLine(suffix) && !stops.Contains("\n") ? [.. stops, "\n"] : stops;

    /// <summary>The text to insert at the caret, given the <paramref name="suffix"/> that follows it.</summary>
    public static string Finish(string completion, string suffix)
    {
        if (string.IsNullOrEmpty(completion)) return completion;

        var newline    = suffix.IndexOf('\n');
        var restOfLine = RestOfLine(suffix);

        if (IsMidLine(suffix))
        {
            var cut  = completion.IndexOf('\n');
            var line = (cut < 0 ? completion : completion[..cut]).TrimEnd('\r');
            // A line that closes nothing it did not open repeats nothing of the line it is inserted in.
            if (!Balance(line).ClosesInExcess) return line;
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

        // ⚠ A model that does not stop writes the gap, then the lines that follow the caret, then whatever comes
        // after them (another method, a Markdown fence, prose): trimming the completion's LAST lines finds nothing to
        // trim, and the whole run-on is inserted. Cut where it STARTS repeating what follows — the first
        // min(2, following) lines, in order —, never at its first line: a completion that is only the repeat is the
        // case of the tail rule below.
        var repeat = Math.Min(2, following.Count);
        for (var i = 1; repeat > 0 && i + repeat <= lines.Count; i++)
        {
            if (!lines.Skip(i).Take(repeat).Select(l => l.Trim()).SequenceEqual(following.Take(repeat), StringComparer.Ordinal))
                continue;
            var kept = string.Join('\n', lines.Take(i)).TrimEnd();
            // Repeated lines that close what the kept part opened are the completion's own closers, not a repetition.
            if (!Balance(kept).LeavesOpenMoreThan(Balance(string.Join('\n', lines.Take(i + repeat))))) return kept;
        }

        var whole = Balance(completion);
        for (var k = Math.Min(lines.Count, following.Count); k > 0; k--)
        {
            var tail = lines.Skip(lines.Count - k).Select(l => l.Trim()).ToList();
            if (!tail.SequenceEqual(following.Take(k), StringComparer.Ordinal)) continue;
            var kept = string.Join('\n', lines.Take(lines.Count - k)).TrimEnd();
            // Lines that close what the completion itself opened are its own, not a repetition: try fewer.
            if (!Balance(kept).LeavesOpenMoreThan(whole)) return kept;
        }
        return completion;
    }

    /// <summary>Each bracket kind's openers minus closers, outside double-quoted strings, and whether a string is left
    /// open.</summary>
    private readonly record struct Brackets(int Round, int Square, int Curly, bool OpenString)
    {
        public bool ClosesInExcess => Round < 0 || Square < 0 || Curly < 0 || OpenString;

        public bool LeavesOpenMoreThan(Brackets whole) =>
            Round > Math.Max(0, whole.Round) || Square > Math.Max(0, whole.Square) || Curly > Math.Max(0, whole.Curly);
    }

    private static Brackets Balance(string text)
    {
        int round = 0, square = 0, curly = 0;
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;                 // an escaped character ends nothing
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '(': round++;  break;
                case ')': round--;  break;
                case '[': square++; break;
                case ']': square--; break;
                case '{': curly++;  break;
                case '}': curly--;  break;
            }
        }
        return new(round, square, curly, inString);
    }
}
