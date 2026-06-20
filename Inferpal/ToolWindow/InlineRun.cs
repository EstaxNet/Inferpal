using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

[DataContract]
internal sealed class InlineRun : NotifyPropertyChangedObject
{
    [DataMember] public string Text     { get; init; } = "";
    [DataMember] public bool   IsBold   { get; init; }
    [DataMember] public bool   IsItalic { get; init; }
    [DataMember] public bool   IsCode   { get; init; }

    private string _foreground = "#D4D4D4";
    [DataMember] public string Foreground { get => _foreground; set => SetProperty(ref _foreground, value); }
}
