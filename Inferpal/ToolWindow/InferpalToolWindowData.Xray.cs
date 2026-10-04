using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

internal partial class InferpalToolWindowData
{
    #region Context X-Ray panel (interactive /xray V2)

    // The panel overlays the messages list. Data comes from XRayPanelPresenter (Core) — the VM
    // only maps the neutral model to cross-boundary rows and applies the per-section toggles to
    // the next turn's system prompt (same _history[0] replacement as the plan-mode toggle).

    [DataMember] public ObservableCollection<XRaySectionItem> XraySections { get; } = [];

    [DataMember] public bool   IsXrayPanelOpen  { get => _isXrayPanelOpen;  set => SetProperty(ref _isXrayPanelOpen,  value); }
    [DataMember] public bool   HasXrayWarning   { get => _hasXrayWarning;   set => SetProperty(ref _hasXrayWarning,   value); }
    [DataMember] public string XrayTotalText    { get => _xrayTotalText;    set => SetProperty(ref _xrayTotalText,    value); }
    [DataMember] public string XrayHistoryText  { get => _xrayHistoryText;  set => SetProperty(ref _xrayHistoryText,  value); }
    [DataMember] public string XrayPanelHint    { get => _xrayPanelHint;    set => SetProperty(ref _xrayPanelHint,    value); }
    [DataMember] public string XrayPanelWarning { get => _xrayPanelWarning; set => SetProperty(ref _xrayPanelWarning, value); }
    [DataMember] public string BtnXrayCopy      { get => _btnXrayCopy;      set => SetProperty(ref _btnXrayCopy,      value); }
    [DataMember] public string TooltipXrayClose { get => _tooltipXrayClose; set => SetProperty(ref _tooltipXrayClose, value); }

    /// <summary>Opens/closes the panel (bound to the context gauge and the panel's ✕ button).</summary>
    [DataMember] public AsyncCommand ToggleXrayPanelCommand { get; }
    /// <summary>Copies the exact system prompt of the next turn (enabled sections only).</summary>
    [DataMember] public AsyncCommand CopyXrayPromptCommand  { get; }

    private Task ToggleXrayPanelAsync(object? _, CancellationToken __) => RunOnVMContextAsync(() =>
    {
        if (!IsXrayPanelOpen) RefreshXrayPanel();   // rebuild on open so the data is always current
        IsXrayPanelOpen = !IsXrayPanelOpen;
    });

    private Task CopyXrayPromptAsync(object? _, CancellationToken __) => RunOnVMContextAsync(() =>
        ClipboardHelper.TrySet(_xrayRawPrompt, "Xray.CopyPrompt"));

    /// <summary>The prompt's composition as the next turn would send it, counted. VM thread only: the history lives there.</summary>
    private XRayPanelModel BuildXrayModel()
    {
        var root     = FindProjectRoot();
        var sections = new SystemPromptBuilder(_config, EditorName, ContextWindowInUse, _indexService.RootDir).BuildSections(
            ModelPrompts.SystemPrompt, PersonaLanguage, _activeTemplateSuffix, root, ActiveFileRelativeTo(root),
            _xrayDisabledSections);
        return XRayPanelPresenter.Build(
            sections, _xrayDisabledSections,
            AgentOrchestrator.EstimateConversationTokens(_history), ContextWindowInUse,
            toolTokens: NextTurnToolTokens());
    }

    /// <summary>The counts the settings' Context page shows, read on the VM context.</summary>
    private async Task<XRayPanelModel?> CurrentXrayModelAsync()
    {
        XRayPanelModel? model = null;
        await RunOnVMContextAsync(() => model = BuildXrayModel());
        return model;
    }

    /// <summary>The settings' "Open Context X-Ray": the panel opens with the counts of now.</summary>
    private Task OpenXrayFromSettingsAsync() => RunOnVMContextAsync(() =>
    {
        RefreshXrayPanel();
        IsXrayPanelOpen = true;
    });

    /// <summary>Rebuilds the panel rows from the current prompt composition. VM thread only.</summary>
    private void RefreshXrayPanel()
    {
        var model = BuildXrayModel();

        _xrayRawPrompt  = model.RawPrompt;
        XrayTotalText   = Strings.XrayHeader($"~{model.TotalTokens:N0}");
        XrayHistoryText = Strings.XrayHistory($"~{model.HistoryTokens:N0}")
                        + (model.ToolTokens > 0 ? "\n" + Strings.XrayTools($"~{model.ToolTokens:N0}") : "");
        HasXrayWarning  = model.OverheadWarning;

        var palette = ThemePalette.For(_isDark, _isHighContrast);
        XraySections.Clear();
        foreach (var s in model.Sections)
            XraySections.Add(new XRaySectionItem(s, palette, OnXraySectionToggled));
    }

    // Row-toggle callback (raised from the row's AsyncCommand, i.e. on the VM context): applies
    // the new disabled set to the next turn's system prompt, mirroring the plan-mode toggle.
    private void OnXraySectionToggled(XRaySectionItem item)
    {
        if (item.Enabled) _xrayDisabledSections.Remove(item.Id);
        else              _xrayDisabledSections.Add(item.Id);

        RefreshSystemPrompt();

        RefreshXrayTotals();
    }

    // Recomputes header totals + raw prompt without rebuilding the rows (keeps expansion state).
    private void RefreshXrayTotals()
    {
        var model = BuildXrayModel();

        _xrayRawPrompt  = model.RawPrompt;
        XrayTotalText   = Strings.XrayHeader($"~{model.TotalTokens:N0}");
        HasXrayWarning  = model.OverheadWarning;
    }

    #endregion
}
