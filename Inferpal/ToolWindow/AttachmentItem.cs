using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

[DataContract]
internal sealed class AttachmentItem : NotifyPropertyChangedObject
{
    private string _background  = "#2D3048";
    private string _foreground  = "#9CDCFE";
    private string _borderColor = "#4A4E7A";

    [DataMember] public string Label       { get; }
    [DataMember] public string Background  { get => _background;  set => SetProperty(ref _background,  value); }
    [DataMember] public string Foreground  { get => _foreground;  set => SetProperty(ref _foreground,  value); }
    [DataMember] public string BorderColor { get => _borderColor; set => SetProperty(ref _borderColor, value); }

    [DataMember] public AsyncCommand RemoveCommand { get; }

    /// <summary>Promotes this attachment's source file to a persistent pinned file.
    /// Only wired (and the 📌 button only shown) when <see cref="CanPin"/> is true.</summary>
    [DataMember] public AsyncCommand PinCommand { get; }

    /// <summary><c>true</c> when this attachment has a real on-disk source path and can be
    /// promoted to a pinned file. False for selection snippets and synthetic chips.</summary>
    [DataMember] public bool CanPin { get; }

    // Not [DataMember] — stays in the extension process only
    public string  Content      { get; }
    public bool    IsAutoAttach { get; }
    public string? SourcePath   { get; }

    public AttachmentItem(string label, string content, Action onRemove,
        bool isAutoAttach = false, string? sourcePath = null, Action? onPin = null)
    {
        Label         = label;
        Content       = content;
        IsAutoAttach  = isAutoAttach;
        SourcePath    = sourcePath;
        CanPin        = onPin is not null && !string.IsNullOrEmpty(sourcePath);
        RemoveCommand = new AsyncCommand((_, _) => { onRemove(); return Task.CompletedTask; });
        PinCommand    = new AsyncCommand((_, _) => { onPin?.Invoke(); return Task.CompletedTask; });

        if (isAutoAttach)
        {
            Background  = "#1A2E40";
            Foreground  = "#6AAEDC";
            BorderColor = "#2A5A80";
        }
    }
}
