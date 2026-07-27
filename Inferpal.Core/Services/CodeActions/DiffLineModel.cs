namespace Inferpal.Services.CodeActions;

/// <summary>
/// Editor-agnostic line of a computed diff (<see cref="DiffComputer.Compute"/>). Plain data,
/// no VS dependency — the tool window maps these to its Remote-UI observable type
/// (<c>Inferpal.ToolWindow.DiffLine</c>). Colors are plain hex strings so any front-end
/// can render them directly.
/// </summary>
internal sealed class DiffLineModel
{
    /// <summary>"+" added, "-" removed, " " context, "…" collapsed/oversize marker.</summary>
    public string Prefix     { get; init; } = " ";
    public string Text       { get; init; } = "";
    public string Background { get; init; } = "Transparent";
    public string Foreground { get; init; } = "#808080";
}
