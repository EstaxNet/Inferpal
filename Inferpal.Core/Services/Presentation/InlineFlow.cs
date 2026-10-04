namespace Inferpal.Services.Presentation;

/// <summary>
/// Cuts a paragraph's formatted runs into the pieces Visual Studio's chat lays out. Remote UI cannot bind a
/// TextBlock's inlines, so a paragraph that mixes text, <c>code</c> and bold is one TextBlock per piece in a
/// WrapPanel — and a WrapPanel only breaks a line BETWEEN its children. A whole run as a child turns every run
/// boundary into the only place a line can end: a run that does not fit the rest of the line drops whole to the
/// next one, so the end of a sentence lands under its code span and a list bullet sits alone above a long
/// identifier. Every place a line may end must therefore be a piece boundary: one piece per word, its trailing
/// spaces kept with it.
/// </summary>
internal static class InlineFlow
{
    /// <summary>
    /// The pieces of one paragraph. Adjacent runs with the same formatting are joined first: a paragraph whose text
    /// is uniform stays ONE piece, which wraps inside itself at no cost. A hard line break becomes a break piece,
    /// which the view stretches to the panel's width — a TextBlock holding "\n" is merely a taller child.
    /// </summary>
    public static List<InlinePiece> Pieces(IReadOnlyList<InlineRunModel> runs)
    {
        var joined = Join(runs);
        var pieces = new List<InlinePiece>();
        if (joined.Count == 1 && !joined[0].Text.Contains('\n'))
        {
            var only = joined[0];
            pieces.Add(new InlinePiece(only.Text, only.IsBold, only.IsItalic, only.IsCode));
            return pieces;
        }

        foreach (var run in joined)
        {
            var lines = run.Text.Split('\n');
            for (var l = 0; l < lines.Length; l++)
            {
                if (l > 0) pieces.Add(InlinePiece.Break);
                var words = Words(lines[l]);
                for (var w = 0; w < words.Count; w++)
                    pieces.Add(new InlinePiece(words[w], run.IsBold, run.IsItalic, run.IsCode)
                    {
                        // A code span cut into words keeps ONE padded background: inner edges carry no padding.
                        StartsSpan = w == 0,
                        EndsSpan   = w == words.Count - 1,
                    });
            }
        }
        return pieces;
    }

    private static List<InlineRunModel> Join(IReadOnlyList<InlineRunModel> runs)
    {
        var joined = new List<InlineRunModel>();
        foreach (var run in runs)
        {
            if (run.Text.Length == 0) continue;
            if (joined.Count > 0 && joined[^1] is var last
                && last.IsBold == run.IsBold && last.IsItalic == run.IsItalic && last.IsCode == run.IsCode)
                joined[^1] = new InlineRunModel { Text = last.Text + run.Text, IsBold = run.IsBold, IsItalic = run.IsItalic, IsCode = run.IsCode };
            else
                joined.Add(run);
        }
        return joined;
    }

    /// <summary>
    /// "a bc  d" → "a ", "bc  ", "d": a word with the spaces after it, where a line may end. Spaces that open the
    /// text are a piece of their own. Only a space or a tab is a break: a non-breaking space joins, as in text.
    /// </summary>
    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && !IsBreakingSpace(text[i])) i++;
            while (i < text.Length && IsBreakingSpace(text[i])) i++;
            words.Add(text[start..i]);
            start = i;
        }
        return words;
    }

    private static bool IsBreakingSpace(char c) => c is ' ' or '\t';
}

/// <summary>One child of the chat's WrapPanel: a word with its formatting, or a forced line break.</summary>
internal sealed record InlinePiece(string Text, bool IsBold, bool IsItalic, bool IsCode)
{
    /// <summary>A hard line break: an empty piece the view stretches across the panel.</summary>
    public static InlinePiece Break => new("", false, false, false) { IsBreak = true };

    public bool IsBreak { get; init; }

    /// <summary>For a code piece: whether it opens / closes its code span, where the background's padding goes.</summary>
    public bool StartsSpan { get; init; } = true;
    public bool EndsSpan   { get; init; } = true;
}
