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
}
