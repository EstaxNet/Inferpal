using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>
/// One row in a generic editable settings list (pinned context files, custom slash commands,
/// custom agent tools). Holds an enable toggle, a primary <see cref="Title"/> + secondary
/// <see cref="Summary"/> shown in the row, the raw editable values (<see cref="Field1"/> /
/// <see cref="Field2"/>) used to repopulate the inline editor, and per-row Edit/Delete commands.
/// </summary>
/// <remarks>
/// Themed brushes and tooltips are shared by all rows: they live on the root VM and are bound via
/// <c>ElementName=root</c>, like <see cref="McpServerRow"/>'s. The parent rebuilds the whole
/// collection from the persisted text on every add/edit/delete (see
/// <c>InferpalSettingsData.BuildPinnedRows</c> and friends) — the text on disk is the source of
/// truth, and rebuilding keeps the two from drifting.
/// <para>
/// ⚠ That rebuild is a choice, not a platform constraint: a bound row CAN be updated in place
/// (<see cref="McpServerRow"/> does it on every status refresh). ⚠ And the choice does not travel:
/// these lists are <c>ItemsControl</c>s with no selection, whereas rebuilding a collection bound to
/// a <c>SelectedItem</c> is the defect <c>SelectionPreservingList</c> exists to prevent. Deliberately no member named <c>Name</c> (reserved in RemoteUI
/// serialization — it renders blank).
/// </remarks>
[DataContract]
internal sealed class EditableListRow : NotifyPropertyChangedObject
{
    private bool   _enabled = true;
    private string _label   = string.Empty;
    private string _summary = string.Empty;

    /// <summary>Raw editable value #1 (path / command name / tool name). Not bound to the template.</summary>
    internal string Field1 = string.Empty;

    /// <summary>Raw editable value #2 (prompt text / shell command). Empty for single-field rows.</summary>
    internal string Field2 = string.Empty;

    internal Action<EditableListRow>? OnEdit;
    internal Action<EditableListRow>? OnDelete;

    public EditableListRow()
    {
        EditCommand   = new AsyncCommand((_, _) => { OnEdit?.Invoke(this);   return Task.CompletedTask; });
        DeleteCommand = new AsyncCommand((_, _) => { OnDelete?.Invoke(this); return Task.CompletedTask; });
    }

    /// <summary>Whether this entry is active. Bound TwoWay to the row checkbox.</summary>
    [DataMember] public bool   Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }

    /// <summary>Primary line shown in the row (file name / command name / tool name).</summary>
    [DataMember] public string Label   { get => _label;   set => SetProperty(ref _label,   value); }

    /// <summary>Secondary one-line preview shown under the title (full path / prompt / command).</summary>
    [DataMember] public string Summary { get => _summary; set => SetProperty(ref _summary, value); }

    [DataMember] public AsyncCommand EditCommand   { get; }
    [DataMember] public AsyncCommand DeleteCommand { get; }
}
