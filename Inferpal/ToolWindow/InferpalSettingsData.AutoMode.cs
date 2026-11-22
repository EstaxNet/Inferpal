using System.Runtime.Serialization;
using Inferpal.Localization;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>
/// Approvals, drawn as two cards — Ask me, Auto — with what auto mode risks said under them as soon as it is picked, and a
/// confirmation in the window when a Save turns approvals off. The same texts as the VS Code panel (the Core's
/// <c>Strings</c>, served there by <c>settings/strings</c>).
/// </summary>
internal partial class InferpalSettingsData
{
    /// <summary>Auto picked: approvals off. The other face of <see cref="AskBeforeActions"/>, which Save reads.</summary>
    [DataMember]
    public bool ApprovalsAuto
    {
        get => !AskBeforeActions;
        set => AskBeforeActions = !value;
    }

    private bool _showAutoConfirm, _autoConfirmTicked;

    /// <summary>The confirmation over the window, shown when a Save turns approvals off.</summary>
    [DataMember] public bool ShowAutoConfirm { get => _showAutoConfirm; set => SetProperty(ref _showAutoConfirm, value); }

    /// <summary>"I understand" ticked: only then does the confirmation's button turn approvals off.</summary>
    [DataMember] public bool AutoConfirmTicked { get => _autoConfirmTicked; set => SetProperty(ref _autoConfirmTicked, value); }

    [DataMember] public string ApprovalsAskTitle { get; private set; } = "";
    [DataMember] public string ApprovalsAskText { get; private set; } = "";
    [DataMember] public string ApprovalsAutoTitle { get; private set; } = "";
    [DataMember] public string ApprovalsAutoText { get; private set; } = "";
    [DataMember] public string AutoRisksTitle { get; private set; } = "";
    [DataMember] public string AutoRiskMistake { get; private set; } = "";
    [DataMember] public string AutoRiskSteered { get; private set; } = "";
    [DataMember] public string AutoRiskOutside { get; private set; } = "";
    [DataMember] public string AutoRiskSmallModel { get; private set; } = "";
    [DataMember] public string AutoStillProtectedTitle { get; private set; } = "";
    [DataMember] public string AutoStillProtected { get; private set; } = "";
    [DataMember] public string AutoConfirmTitle { get; private set; } = "";
    [DataMember] public string AutoConfirmBody { get; private set; } = "";
    [DataMember] public string AutoConfirmCheck { get; private set; } = "";
    [DataMember] public string AutoConfirmKeep { get; private set; } = "";
    [DataMember] public string AutoConfirmTurnOff { get; private set; } = "";

    /// <summary>The confirmation's "Turn approvals off": saves as Save would have.</summary>
    [DataMember] public AsyncCommand ConfirmAutoCommand { get; private set; } = null!;

    /// <summary>The confirmation's "Keep asking me": nothing saved, the cards back on Ask me.</summary>
    [DataMember] public AsyncCommand KeepAskingCommand { get; private set; } = null!;

    /// <summary>The texts, and the two answers of the confirmation. Called with the other labels.</summary>
    private void ApplyAutoModeLabels()
    {
        ApprovalsAskTitle = Strings.ApprovalsAskTitle;
        ApprovalsAskText = Strings.ApprovalsAskText;
        ApprovalsAutoTitle = Strings.ApprovalsAutoTitle;
        ApprovalsAutoText = Strings.ApprovalsAutoText;
        AutoRisksTitle = Strings.AutoRisksTitle;
        AutoRiskMistake = Strings.AutoRiskMistake;
        AutoRiskSteered = Strings.AutoRiskSteered;
        AutoRiskOutside = Strings.AutoRiskOutside;
        AutoRiskSmallModel = Strings.AutoRiskSmallModel;
        AutoStillProtectedTitle = Strings.AutoStillProtectedTitle;
        AutoStillProtected = Strings.AutoStillProtected;
        AutoConfirmTitle = Strings.AutoConfirmTitle;
        AutoConfirmBody = Strings.AutoConfirmBody;
        AutoConfirmCheck = Strings.AutoConfirmCheck;
        AutoConfirmKeep = Strings.AutoConfirmKeep;
        AutoConfirmTurnOff = Strings.AutoConfirmTurnOff;
        foreach (var name in (string[])[nameof(ApprovalsAskTitle), nameof(ApprovalsAskText), nameof(ApprovalsAutoTitle), nameof(ApprovalsAutoText),
                     nameof(AutoRisksTitle), nameof(AutoRiskMistake), nameof(AutoRiskSteered), nameof(AutoRiskOutside), nameof(AutoRiskSmallModel),
                     nameof(AutoStillProtectedTitle), nameof(AutoStillProtected), nameof(AutoConfirmTitle), nameof(AutoConfirmBody),
                     nameof(AutoConfirmCheck), nameof(AutoConfirmKeep), nameof(AutoConfirmTurnOff)])
            RaiseNotifyPropertyChangedEvent(name);
    }

    /// <summary>The confirmation's two commands, made with the view model: a command Visual Studio has not read at its first
    /// read of the view model leaves its button doing nothing.</summary>
    private void InitAutoMode()
    {
        ConfirmAutoCommand = new AsyncCommand(async (_, ct) =>
        {
            if (!AutoConfirmTicked) return;
            await RunOnVMContextAsync(() => { ShowAutoConfirm = false; AutoConfirmTicked = false; });
            try { await SaveCoreAsync(ct); }
            catch (Exception ex)
            {
                Services.Diagnostics.Swallow("Settings.SaveAuto", ex);
                var message = ex.Message;
                await RunOnVMContextAsync(() => SaveStatus = Strings.SettingsSaveFailed(message));
            }
        });
        KeepAskingCommand = new AsyncCommand((_, _) => RunOnVMContextAsync(() =>
        {
            ShowAutoConfirm = false;
            AutoConfirmTicked = false;
            AskBeforeActions = true;
        }));
    }

    /// <summary>Whether this Save turns approvals off: the confirmation is asked first, and the save waits for it.</summary>
    private bool TurnsApprovalsOff() => !AskBeforeActions && !_config.SharedSecurityAlertsDisabled;
}
