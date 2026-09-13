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
    /// <remarks>
    /// Active entries past <see cref="MaxPinned"/> are kept too: the settings window sets no cap, the chip
    /// strip never showed them, and it cannot decide anything about files it does not display.
    /// </remarks>
    public static string Serialize(IEnumerable<string> activePaths, string? previousConfig)
    {
        var chips = activePaths.ToList();
        var lines = (previousConfig ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hidden = lines
            .Where(l => !l.StartsWith('#'))
            .Skip(MaxPinned)
            .Where(l => !chips.Contains(l, StringComparer.OrdinalIgnoreCase));
        var disabled = lines.Where(l => l.StartsWith('#'));
        return string.Join("\n", chips.Concat(hidden).Concat(disabled));
    }

    /// <summary>
    /// The pinned-files setting after an editor's change: <paramref name="live"/>, minus the lines the
    /// editor removed since <paramref name="opened"/>, plus the lines it added.
    /// </summary>
    /// <remarks>
    /// The settings window and the chat strip write the same setting. A window that rewrote it from its
    /// rows erased a file pinned from the chat after it opened. Lines compare case-insensitively, like
    /// the chips.
    /// </remarks>
    public static string MergeEdits(string? live, string? opened, string? edited)
    {
        static List<string> Lines(string? text) => (text ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var before  = Lines(opened);
        var after   = Lines(edited);
        var removed = before.Where(l => !after.Contains(l, StringComparer.OrdinalIgnoreCase)).ToList();
        var added   = after.Where(l => !before.Contains(l, StringComparer.OrdinalIgnoreCase));

        var result = Lines(live).Where(l => !removed.Contains(l, StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var line in added)
            if (!result.Contains(line, StringComparer.OrdinalIgnoreCase)) result.Add(line);
        return string.Join("\n", result);
    }
}
