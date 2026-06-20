using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

[DataContract]
internal sealed class TableCell : NotifyPropertyChangedObject
{
    [DataMember] public string Text     { get; init; } = "";
    [DataMember] public bool   IsHeader { get; init; }

    private string _themeText = "#D4D4D4";
    private string _themeBg   = "Transparent";

    [DataMember] public string ThemeText { get => _themeText; set => SetProperty(ref _themeText, value); }
    [DataMember] public string ThemeBg   { get => _themeBg;   set => SetProperty(ref _themeBg,   value); }
}
