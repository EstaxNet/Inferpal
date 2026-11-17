using System.Runtime.Serialization;

namespace Inferpal.ToolWindow;

/// <summary>The text read by OCR that went instead of an image, as the question shows it: its label and the text the model
/// got (pari 108).</summary>
[DataContract]
internal sealed class ImageTextItem
{
    public ImageTextItem(string label, string text) => (Label, Text) = (label, text);

    [DataMember] public string Label { get; }
    [DataMember] public string Text  { get; }
}
