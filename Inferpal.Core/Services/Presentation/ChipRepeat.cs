namespace Inferpal.Services.Presentation;

/// <summary>
/// Whether a chip about to be added repeats one already in the composer — the same file dropped twice, picked again with
/// @, attached again from the Explorer: a second chip would send its text twice. Visual Studio asks it in the one funnel
/// every chip goes through (<c>AddAttachment</c>); VS Code's <c>addChip</c> applies the same rule.
/// </summary>
internal static class ChipRepeat
{
    /// <summary>The same whole file (by <see cref="PathComparer"/>: its text may have changed since), or, for a chip that
    /// holds no whole file — a folder, a selection, a mention — the same text under the same name.</summary>
    internal static bool Repeats(string label, string content, string? sourcePath,
                                 string otherLabel, string otherContent, string? otherSourcePath) =>
        sourcePath is not null && otherSourcePath is not null
            ? PathComparer.Default.Equals(sourcePath, otherSourcePath)
            : label == otherLabel && content == otherContent;
}
