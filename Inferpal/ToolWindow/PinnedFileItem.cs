using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>
/// A persistent context file pinned by the user. Unlike <see cref="AttachmentItem"/>
/// (transient, cleared after each send), a pinned file survives sends and is stored in
/// config (<c>PinnedContextFiles</c>); its content is re-read fresh and injected into the
/// system prompt before every request. Rendered as a gold 📌 chip in the input card.
/// </summary>
[DataContract]
internal sealed class PinnedFileItem : NotifyPropertyChangedObject
{
    private string _background  = "#3A2E1A";
    private string _foreground  = "#E0B050";
    private string _borderColor = "#7A5A2A";

    [DataMember] public string Label       { get; }
    [DataMember] public string Background  { get => _background;  set => SetProperty(ref _background,  value); }
    [DataMember] public string Foreground  { get => _foreground;  set => SetProperty(ref _foreground,  value); }
    [DataMember] public string BorderColor { get => _borderColor; set => SetProperty(ref _borderColor, value); }

    [DataMember] public AsyncCommand RemoveCommand { get; }

    // Not [DataMember] — the absolute path stays in the extension process only.
    public string Path { get; }

    public PinnedFileItem(string path, string label, Action onRemove)
    {
        Path          = path;
        Label         = label;
        RemoveCommand = new AsyncCommand((_, _) => { onRemove(); return Task.CompletedTask; });
    }
}
