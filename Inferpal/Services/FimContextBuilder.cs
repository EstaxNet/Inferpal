using System.Collections.Generic;
using Microsoft.VisualStudio.Extensibility.Editor;

namespace Inferpal.Services;

/// <summary>
/// Builds the prefix/suffix context for Fill-in-the-Middle (FIM) completions.
/// </summary>
/// <remarks>
/// Asymmetric ratio: 64 lines before the cursor vs 16 lines after.
/// Code before the cursor is 4× more informative than code after it —
/// the model needs causal context, not lookahead. Validated by LocalPilot.
/// </remarks>
internal static class FimContextBuilder
{
    private const int LinesBefore = 64;
    private const int LinesAfter  = 16;

    // ── Performance presets ────────────────────────────────────────────────────

    public record InlineCompletionSettings(int MaxTokens, double Temperature, int DebounceMs);

    private static readonly Dictionary<string, InlineCompletionSettings> Presets = new()
    {
        ["Fast"]         = new(128,  0.4,  300),
        ["Default"]      = new(256,  0.2,  600),
        ["HighAccuracy"] = new(512,  0.1, 1000),
    };

    public static InlineCompletionSettings GetSettings(string mode) =>
        Presets.TryGetValue(mode, out var s) ? s : Presets["Default"];

    // ── IntelliSense suppression ───────────────────────────────────────────────

    // Characters that trigger VS IntelliSense — FIM must not fire after these
    // because the native completion dropdown would already be open.
    private static readonly HashSet<char> IntelliSenseTriggers =
        ['.', '(', '[', '<', '"', '\'', ',', ' '];

    public record FimContext(string Prefix, string Suffix);

    /// <summary>
    /// Returns true when VS IntelliSense is likely active and FIM should be suppressed.
    /// The OOP extensibility SDK (17.14) exposes no API to query the completion dropdown
    /// state directly, so we use trigger-character detection as a reliable proxy.
    /// </summary>
    public static bool ShouldSuppress(ITextViewSnapshot view)
    {
        var text   = view.Document.Text.CopyToString();
        var cursor = view.Selection.InsertionPosition.Offset;

        if (cursor == 0) return false;

        return IntelliSenseTriggers.Contains(text[cursor - 1]);
    }

    public static FimContext Build(ITextViewSnapshot view)
    {
        var text   = view.Document.Text.CopyToString();
        var cursor = view.Selection.InsertionPosition.Offset;

        var prefix = text[..cursor];
        var suffix = text[cursor..];

        return new FimContext(
            Prefix: TailLines(prefix, LinesBefore),
            Suffix: HeadLines(suffix, LinesAfter)
        );
    }

    // Keeps the last `count` newline-separated fragments (= `count - 1` full lines
    // plus the partial line up to the cursor).
    private static string TailLines(string text, int count)
    {
        var span  = text.AsSpan();
        var found = 0;
        var idx   = span.Length;

        while (idx > 0 && found < count)
        {
            idx--;
            if (span[idx] == '\n')
                found++;
        }

        // Align: if we stopped on a '\n' and didn't exhaust the string, skip it
        if (found == count && idx < span.Length && span[idx] == '\n')
            idx++;

        return text[idx..];
    }

    // Keeps the first `count` newline-separated fragments (= the rest of the
    // current line plus `count` full lines after the cursor).
    private static string HeadLines(string text, int count)
    {
        var span  = text.AsSpan();
        var found = 0;
        var idx   = 0;

        while (idx < span.Length && found < count)
        {
            if (span[idx] == '\n')
                found++;
            idx++;
        }

        return text[..idx];
    }
}
