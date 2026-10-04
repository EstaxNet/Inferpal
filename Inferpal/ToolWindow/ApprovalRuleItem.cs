using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>
/// One row of the approval rules table of the settings window, built from the Core's
/// <see cref="Services.Presentation.ApprovalRuleTable"/> — the presenter the VS Code panel reads too.
/// </summary>
/// <remarks>Immutable once built: the table is rebuilt whenever the rules text changes.</remarks>
[DataContract]
internal sealed class ApprovalRuleItem : NotifyPropertyChangedObject
{
    public ApprovalRuleItem(Services.Presentation.ApprovalRuleRow row, Action<int>? onDelete)
    {
        EffectText = row.EffectText;
        IsAllow    = row.Allow == true;
        IsDeny     = row.Allow == false;
        Tool       = row.Tool;
        Pattern    = row.Pattern;
        FromText   = row.FromText;
        NoteText   = row.NoteText ?? string.Empty;
        HasNote    = row.NoteText is { Length: > 0 };
        InForce    = row.Status == Services.Presentation.RuleStatus.InForce;
        // Only a machine rule is edited here: the team file ships with the repository.
        CanDelete  = row.Source == Services.Presentation.RuleSource.Machine && row.MachineLine >= 0 && onDelete is not null;
        var line = row.MachineLine;
        DeleteCommand = new AsyncCommand((_, _) => { onDelete?.Invoke(line); return Task.CompletedTask; });
    }

    [DataMember] public string EffectText { get; }
    [DataMember] public bool   IsAllow    { get; }
    [DataMember] public bool   IsDeny     { get; }
    [DataMember] public string Tool       { get; }
    [DataMember] public string Pattern    { get; }
    [DataMember] public string FromText   { get; }
    [DataMember] public string NoteText   { get; }
    [DataMember] public bool   HasNote    { get; }
    /// <summary>False for a rule written but set aside (a team <c>allow</c>, an unreadable line): drawn faded.</summary>
    [DataMember] public bool   InForce    { get; }
    [DataMember] public bool   CanDelete  { get; }
    [DataMember] public AsyncCommand DeleteCommand { get; }
}
