using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using System.Threading;
using Microsoft.VisualStudio.Extensibility.UI;
using Inferpal.Localization;
using Inferpal.Services;

namespace Inferpal.ToolWindow;

/// <summary>
/// Data context of the tool-approval dialog (<see cref="ApprovalDialogControl"/>): the
/// localized message, the colored diff preview and the three decision buttons. The dialog
/// content is immutable once shown; the user's choice completes <see cref="Decision"/>,
/// which <c>VsApprovalService</c> awaits (closing the dialog via its cancellation token).
/// </summary>
[DataContract]
internal sealed class ApprovalDialogData : NotifyPropertyChangedObject
{
    private readonly TaskCompletionSource<ApprovalDecision> _decision =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [DataMember] public string Message { get; }
    [DataMember] public bool   HasDiff { get; }
    [DataMember] public ObservableCollection<DiffLine> DiffLines { get; } = [];

    // Button labels (localized) — also the UIA names automation relies on.
    [DataMember] public string BtnAllowOnce   { get; }
    [DataMember] public string BtnAlwaysAllow { get; }
    [DataMember] public string BtnDeny        { get; }
    /// <summary>The narrow rule the card offers ("Always: commands that start with `dotnet test`"); empty when none.</summary>
    [DataMember] public string BtnRule        { get; }
    [DataMember] public string RuleTip        { get; }
    [DataMember] public bool   HasRule        { get; }

    // Theme colours as plain strings, same pattern as the chat VM (no theme propagation
    // across the Remote UI boundary): resolved once from the last known VS theme.
    [DataMember] public string ThemeText       { get; }
    [DataMember] public string ThemeSubtleText { get; }
    [DataMember] public string ThemeBorder     { get; }
    [DataMember] public string ThemeCodeBg     { get; }

    [DataMember] public AsyncCommand AllowOnceCommand   { get; }
    [DataMember] public AsyncCommand AlwaysAllowCommand { get; }
    [DataMember] public AsyncCommand DenyCommand        { get; }
    [DataMember] public AsyncCommand RuleCommand        { get; }

    /// <summary>Completes with the user's choice; never completes if the dialog is dismissed
    /// (the service then falls back to deny).</summary>
    public Task<ApprovalDecision> Decision => _decision.Task;

    /// <param name="ruleLabel">The narrow rule the card offers, as its button reads (<c>ApprovalCardModel.RuleLabel</c>);
    /// empty when none.</param>
    /// <param name="ruleTip">The rule's lines, for the button's tooltip.</param>
    public ApprovalDialogData(string message, DiffInfo? diff, string ruleLabel = "", string ruleTip = "")
    {
        Message        = message;
        BtnAllowOnce   = Strings.ApprovalAllowOnce;
        BtnAlwaysAllow = Strings.ApprovalAlwaysAllow;
        BtnDeny        = Strings.ApprovalDeny;
        BtnRule        = ruleLabel;
        RuleTip        = ruleTip;
        HasRule        = ruleLabel.Length > 0;

        var palette = ThemePalette.For(VsThemeDetector.CurrentIsDark, VsThemeDetector.HighContrastOn());
        ThemeText       = palette.Text;
        ThemeSubtleText = palette.SubtleText;
        ThemeBorder     = palette.Border;
        ThemeCodeBg     = palette.CodeBg;

        if (diff is not null)
        {
            foreach (var line in DiffComputer.Compute(diff.OldText, diff.NewText))
                DiffLines.Add(DiffLine.FromModel(line));
            DiffLine.ApplyTheme(DiffLines, palette);
        }
        HasDiff = DiffLines.Count > 0;

        AllowOnceCommand   = new AsyncCommand((_, _) => { _decision.TrySetResult(ApprovalDecision.Once);   return Task.CompletedTask; });
        AlwaysAllowCommand = new AsyncCommand((_, _) => { _decision.TrySetResult(ApprovalDecision.Always); return Task.CompletedTask; });
        DenyCommand        = new AsyncCommand((_, _) => { _decision.TrySetResult(ApprovalDecision.Deny);   return Task.CompletedTask; });
        RuleCommand        = new AsyncCommand((_, _) =>
        {
            if (HasRule) _decision.TrySetResult(ApprovalDecision.Rule);
            return Task.CompletedTask;
        });
    }
}
