using System.Runtime.Serialization;
using System.Threading;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>
/// One entry in the typed @-mention autocomplete popup. Represents either a context
/// category (<c>@file</c>, <c>@code</c>, <c>@folder</c>, <c>@clipboard</c>, <c>@tree</c>,
/// <c>@diff</c>, <c>@problems</c>) or a concrete item within a category (a file, a folder,
/// a semantic-search action). The behaviour on selection is supplied by the caller.
/// </summary>
[DataContract]
internal sealed class MentionSuggestion : NotifyPropertyChangedObject
{
    /// <summary>Primary line — the @token, a filename, or an action label.</summary>
    [DataMember] public string Label        { get; }

    /// <summary>Secondary line — a description or a relative path.</summary>
    [DataMember] public string SubLabel     { get; }

    [DataMember] public string ThemeText    { get; }
    [DataMember] public string ThemeSubText { get; }
    [DataMember] public AsyncCommand SelectCommand { get; }

    /// <summary>
    /// The same action the <see cref="SelectCommand"/> button runs. Exposed so that the
    /// view-model can trigger the first suggestion from the keyboard (Enter) — the popup
    /// item is otherwise only reachable by mouse click. Not serialized to the RemoteUI client.
    /// </summary>
    public Func<CancellationToken, Task> OnSelect { get; }

    public MentionSuggestion(
        string label,
        string subLabel,
        string themeText,
        string themeSubText,
        Func<CancellationToken, Task> onSelect)
    {
        Label        = label;
        SubLabel     = subLabel;
        ThemeText    = themeText;
        ThemeSubText = themeSubText;
        OnSelect     = onSelect;
        SelectCommand = new AsyncCommand((_, ct) => onSelect(ct));
    }
}
