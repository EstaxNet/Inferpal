using System.Runtime.Serialization;
using System.Threading;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>
/// One row of the mode list above the composer: Chat, Agent or Plan, or a mode found for the workspace — its name, what
/// it is for and, for a mode found here, the model it answers with, what it may do and its file.
/// </summary>
[DataContract]
internal sealed class ModeMenuItem : NotifyPropertyChangedObject
{
    [DataMember] public string Name            { get; }

    /// <summary>" — what it is for", after the name on the row's first line; empty without a description.</summary>
    [DataMember] public string DescriptionTail { get; }

    /// <summary>The model it answers with, what it may do, its file — empty for Inferpal's own three.</summary>
    [DataMember] public string Summary         { get; }
    [DataMember] public bool   HasSummary      { get; }

    /// <summary>The mode the conversation is held in now.</summary>
    [DataMember] public bool   IsChecked       { get; }

    [DataMember] public string ThemeText       { get; }
    [DataMember] public string ThemeSubText    { get; }
    [DataMember] public string ThemeHover      { get; }

    [DataMember] public AsyncCommand SelectCommand { get; }

    public ModeMenuItem(string name, string description, string summary, bool isChecked,
                        string themeText, string themeSubText, string themeHover, Func<CancellationToken, Task> onSelect)
    {
        Name            = name;
        DescriptionTail = string.IsNullOrWhiteSpace(description) ? string.Empty : " — " + description;
        Summary         = summary;
        HasSummary      = summary.Length > 0;
        IsChecked       = isChecked;
        ThemeText       = themeText;
        ThemeSubText    = themeSubText;
        ThemeHover      = themeHover;
        SelectCommand   = new AsyncCommand((_, ct) => onSelect(ct));
    }
}
