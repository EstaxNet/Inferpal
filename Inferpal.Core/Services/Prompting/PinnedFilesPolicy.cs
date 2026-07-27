namespace Inferpal.Services.Prompting;

internal enum PinDecision { Pin, Duplicate, CapReached, Invalid }

/// <summary>
/// Persistence rules for pinned context files (the gold 📌 chips). The config value is a
/// newline-joined list where a leading '#' marks an entry disabled from Settings: the
/// chat strip only shows and edits the active entries, and must round-trip the disabled
/// ones untouched when it saves.
/// </summary>
internal static class PinnedFilesPolicy
{
    public const int MaxPinned = 3;

    /// <summary>The active (chip-visible) paths: non-'#' entries, trimmed, capped.</summary>
    public static List<string> ParseActive(string? config) =>
        (config ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !p.StartsWith('#'))
            .Take(MaxPinned)
            .ToList();

    /// <summary>Whether <paramref name="path"/> (pre-trimmed) can join <paramref name="current"/>.</summary>
    public static PinDecision Decide(IReadOnlyList<string> current, string path)
    {
        if (string.IsNullOrEmpty(path)) return PinDecision.Invalid;
        if (current.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            return PinDecision.Duplicate;
        if (current.Count >= MaxPinned) return PinDecision.CapReached;
        return PinDecision.Pin;
    }

    /// <summary>
    /// Serializes the chip paths back to config, re-appending the disabled ('#') entries
    /// from the previous value so a chip edit never wipes them.
    /// </summary>
    public static string Serialize(IEnumerable<string> activePaths, string? previousConfig)
    {
        var disabled = (previousConfig ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith('#'));
        return string.Join("\n", activePaths.Concat(disabled));
    }
}
