using System.Runtime.Serialization;
using System.Threading;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>
/// One entry in the slash-command autocomplete popup.
/// </summary>
[DataContract]
internal sealed class SlashSuggestion : NotifyPropertyChangedObject
{
    /// <summary>The command token shown in bold (e.g. <c>/explain</c>).</summary>
    [DataMember] public string Command     { get; }

    /// <summary>Short one-line description shown below the command.</summary>
    [DataMember] public string Description { get; }

    [DataMember] public string ThemeText    { get; }
    [DataMember] public string ThemeSubText { get; }

    [DataMember] public AsyncCommand SelectCommand { get; }

    public SlashSuggestion(
        string command,
        string description,
        string themeText,
        string themeSubText,
        Func<string, CancellationToken, Task> onSelect)
    {
        Command      = command;
        Description  = description;
        ThemeText    = themeText;
        ThemeSubText = themeSubText;
        SelectCommand = new AsyncCommand((_, ct) => onSelect(command, ct));
    }
}
