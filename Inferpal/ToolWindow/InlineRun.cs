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

    /// <summary>A hard line break: the view stretches it across the panel so the next piece starts a line.</summary>
    [DataMember] public bool IsBreak    { get; init; }

    /// <summary>For a code piece: whether it opens / closes its code span — only the span's outer edges are padded.</summary>
    [DataMember] public bool StartsSpan { get; init; } = true;
    [DataMember] public bool EndsSpan   { get; init; } = true;

    private string _foreground = "#E8E8EC";
    [DataMember] public string Foreground { get => _foreground; set => SetProperty(ref _foreground, value); }
}
