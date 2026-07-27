namespace Inferpal.Services.CodeActions;

/// <summary>
/// Editor-agnostic line of a computed diff (<see cref="DiffComputer.Compute"/>). Plain data,
/// no VS dependency — the tool window maps these to its Remote-UI observable type
/// (<c>Inferpal.ToolWindow.DiffLine</c>). Carries structure only (prefix + text); colours are
/// applied by the front-end from the active <see cref="Presentation.ThemePalette"/> so the diff
/// renders correctly on any theme and re-tints when the theme changes.
/// </summary>
internal sealed class DiffLineModel
{
    /// <summary>"+" added, "-" removed, " " context, "…" collapsed/oversize marker.</summary>
    public string Prefix { get; init; } = " ";
    public string Text   { get; init; } = "";
}
