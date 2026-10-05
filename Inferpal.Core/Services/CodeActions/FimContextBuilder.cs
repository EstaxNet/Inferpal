using System.Collections.Generic;

namespace Inferpal.Services.CodeActions;

/// <summary>
/// The inline completion (FIM) performance presets: how many tokens, how warm, after how long a pause.
/// </summary>
internal static class FimContextBuilder
{
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
