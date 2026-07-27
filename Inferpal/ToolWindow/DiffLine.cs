using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

[DataContract]
internal sealed class DiffLine : NotifyPropertyChangedObject
{
    [DataMember] public string Prefix     { get; init; } = " ";
    [DataMember] public string Text       { get; init; } = "";
    [DataMember] public string Background { get; init; } = "Transparent";
    [DataMember] public string Foreground { get; init; } = "#808080";

    /// <summary>Maps the editor-agnostic diff line (<see cref="Services.CodeActions.DiffComputer"/>)
    /// to this Remote-UI observable type. Pure mapping, no logic.</summary>
    internal static DiffLine FromModel(DiffLineModel model) =>
        new() { Prefix = model.Prefix, Text = model.Text, Background = model.Background, Foreground = model.Foreground };
}
