using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>
/// One line of the "Review a commit" dialog (<see cref="ReviewCommitDialogData"/>): a commit — short hash, subject, author
/// and age — or the typed text itself, a branch, tag or range. A click selects it; the dialog's Review button reviews the
/// selected line.
/// </summary>
[DataContract]
internal sealed class ReviewCommitRow : NotifyPropertyChangedObject
{
    private readonly string _selectedBackground;
    private readonly string _selectedLine;
    private string _background = "Transparent";
    private string _line = "Transparent";

    [DataMember] public string Hash    { get; }
    [DataMember] public string Subject { get; }
    [DataMember] public string Meta    { get; }
    [DataMember] public string Background { get => _background; private set => SetProperty(ref _background, value); }
    /// <summary>The selected line's bar on its left: a tint alone is too faint on light backgrounds and in high contrast,
    /// where selection is drawn with a line.</summary>
    [DataMember] public string Line       { get => _line;       private set => SetProperty(ref _line, value); }
    [DataMember] public AsyncCommand SelectCommand { get; }

    /// <summary>What <c>/check</c> reviews when this line is chosen: the commit's full hash, or the typed text.</summary>
    public string Revision { get; }

    public ReviewCommitRow(string hash, string subject, string meta, string revision, string selectedBackground,
                           string selectedLine, Action<ReviewCommitRow> select)
    {
        Hash                = hash;
        Subject             = subject;
        Meta                = meta;
        Revision            = revision;
        _selectedBackground = selectedBackground;
        _selectedLine       = selectedLine;
        SelectCommand       = new AsyncCommand((_, _) =>
        {
            select(this);
            return Task.CompletedTask;
        });
    }

    /// <summary>Drawn as the selected line, or not.</summary>
    public void Mark(bool selected)
    {
        Background = selected ? _selectedBackground : "Transparent";
        Line       = selected ? _selectedLine : "Transparent";
    }
}
