using System;
using System.Collections.Generic;

namespace Inferpal.Services.CodeActions;

/// <summary>
/// The inline completion (FIM) performance presets — how many tokens, how warm, after how long a pause — and the
/// context window read around the caret.
/// </summary>
internal static class FimContextBuilder
{
    // ── Context window around the caret ───────────────────────────────────────

    /// <summary>Characters of code kept before the caret.</summary>
    /// <remarks>⚠ Shared by both front-ends: VS Code's <c>inlineCompletions.ts</c> uses the same numbers
    /// (<c>FimContextWindowTests</c>).</remarks>
    public const int MaxPrefixChars = 4000;

    /// <summary>Characters of code kept after the caret. See <see cref="MaxPrefixChars"/>.</summary>
    public const int MaxSuffixChars = 1500;

    /// <summary>The span read around <paramref name="cursor"/>: the lines kept on each side, and never more
    /// characters than <see cref="MaxPrefixChars"/> before and <see cref="MaxSuffixChars"/> after.</summary>
    /// <remarks>⚠ A line budget alone does not bound the text: one line of minified script or of a generated literal
    /// is hundreds of kilobytes — copied on devenv's UI thread at every pause in typing, then sent whole, in a request
    /// the backend refuses for its size or cuts at the head, where the completion template starts.</remarks>
    public static (int Start, int End) Window(int firstLineStart, int cursor, int lastLineEnd) =>
        (Math.Max(firstLineStart, cursor - MaxPrefixChars), Math.Min(lastLineEnd, cursor + MaxSuffixChars));

    // ── Performance presets ────────────────────────────────────────────────────

    public record InlineCompletionSettings(int MaxTokens, double Temperature, int DebounceMs);

    // Case-insensitive: "fast" written by hand names Fast, as both panels read it.
    private static readonly Dictionary<string, InlineCompletionSettings> Presets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Fast"]         = new(128,  0.4,  300),
        ["Default"]      = new(256,  0.2,  600),
        ["HighAccuracy"] = new(512,  0.1, 1000),
    };

    public static InlineCompletionSettings GetSettings(string mode) =>
        Presets.TryGetValue(mode, out var s) ? s : Presets["Default"];
}
