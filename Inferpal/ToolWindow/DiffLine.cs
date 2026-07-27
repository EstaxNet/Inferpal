using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

[DataContract]
internal sealed class DiffLine : NotifyPropertyChangedObject
{
    private string _background = "Transparent";
    private string _foreground = "#808080";

    [DataMember] public string Prefix { get; init; } = " ";
    [DataMember] public string Text   { get; init; } = "";

    // Settable + notifying: assigned from the active ThemePalette by ApplyItemTheme, both at
    // bubble creation and when the VS theme flips while the diff is on screen.
    [DataMember] public string Background { get => _background; set => SetProperty(ref _background, value); }
    [DataMember] public string Foreground { get => _foreground; set => SetProperty(ref _foreground, value); }

    /// <summary>Maps the editor-agnostic diff line (<see cref="Services.CodeActions.DiffComputer"/>)
    /// to this Remote-UI observable type. Pure mapping, no logic — colours come from the theme.</summary>
    internal static DiffLine FromModel(DiffLineModel model) =>
        new() { Prefix = model.Prefix, Text = model.Text };

    /// <summary>
    /// Applies the palette's diff colouring (green-background additions / red-background
    /// deletions) to a set of lines. Shared by the chat tool bubbles (ApplyItemTheme) and
    /// the approval dialog so both render the diff identically.
    /// </summary>
    internal static void ApplyTheme(IEnumerable<DiffLine> lines, ThemePalette palette)
    {
        foreach (var d in lines)
        {
            (d.Background, d.Foreground) = d.Prefix switch
            {
                "+" => (palette.DiffAddBg,    palette.DiffAddText),
                "-" => (palette.DiffRemoveBg, palette.DiffRemoveText),
                _   => ("Transparent",        palette.BubbleSubtleText),
            };
        }
    }
}
