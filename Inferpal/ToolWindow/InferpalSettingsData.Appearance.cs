using System.Runtime.Serialization;
using Inferpal.Localization;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>
/// The Language and appearance page's own blocks: the editor's theme, shown as three cards (the one in use marked) —
/// Inferpal follows it, high contrast included, and never sets it — and the conversation's density.
/// </summary>
internal partial class InferpalSettingsData
{
    private string _chatDensity = "comfortable";
    /// <summary><c>comfortable</c> or <c>compact</c>: the segment the density switch shows selected.</summary>
    [DataMember] public string ChatDensity { get => _chatDensity; set => SetProperty(ref _chatDensity, value); }
    [DataMember] public AsyncCommand SetDensityCommand { get; private set; } = null!;

    private string _labelDensity = string.Empty, _hintDensity = string.Empty, _labelDensityComfortable = string.Empty,
                   _labelDensityCompact = string.Empty, _labelSectionTheme = string.Empty, _themeNote = string.Empty,
                   _themeLightCaption = string.Empty, _themeDarkCaption = string.Empty, _themeHighContrastCaption = string.Empty,
                   _themeInUse = "dark";
    [DataMember] public string LabelDensity             { get => _labelDensity;             set => SetProperty(ref _labelDensity,             value); }
    [DataMember] public string HintDensity              { get => _hintDensity;              set => SetProperty(ref _hintDensity,              value); }
    [DataMember] public string LabelDensityComfortable  { get => _labelDensityComfortable;  set => SetProperty(ref _labelDensityComfortable,  value); }
    [DataMember] public string LabelDensityCompact      { get => _labelDensityCompact;      set => SetProperty(ref _labelDensityCompact,      value); }
    [DataMember] public string LabelSectionTheme        { get => _labelSectionTheme;        set => SetProperty(ref _labelSectionTheme,        value); }
    [DataMember] public string ThemeNote                { get => _themeNote;                set => SetProperty(ref _themeNote,                value); }
    [DataMember] public string ThemeLightCaption        { get => _themeLightCaption;        set => SetProperty(ref _themeLightCaption,        value); }
    [DataMember] public string ThemeDarkCaption         { get => _themeDarkCaption;         set => SetProperty(ref _themeDarkCaption,         value); }
    [DataMember] public string ThemeHighContrastCaption { get => _themeHighContrastCaption; set => SetProperty(ref _themeHighContrastCaption, value); }
    /// <summary><c>light</c>, <c>dark</c> or <c>highContrast</c>: the card drawn as the theme in use.</summary>
    [DataMember] public string ThemeInUse               { get => _themeInUse;               set => SetProperty(ref _themeInUse,               value); }
    private string _segmentSelectedBg = "#2E2B3D";
    /// <summary>The fill of a switch's selected segment — the navigation's selected-page fill.</summary>
    [DataMember] public string SegmentSelectedBg        { get => _segmentSelectedBg;        set => SetProperty(ref _segmentSelectedBg,        value); }

    /// <summary>The page's commands and its saved density; called by the constructor.</summary>
    private void InitAppearance(Config.InferpalConfig saved)
    {
        _chatDensity      = saved.IsCompactChat ? "compact" : "comfortable";
        SetDensityCommand = new AsyncCommand((p, _) => RunOnVMContextAsync(() =>
        {
            if (p is "comfortable" or "compact") ChatDensity = (string)p;
        }));
    }

    /// <summary>The page's words, in the interface language; part of <see cref="ApplyLabels"/>.</summary>
    private void ApplyAppearanceLabels()
    {
        LabelDensity            = Strings.LabelDensity;
        HintDensity             = Strings.HintDensity;
        LabelDensityComfortable = Strings.DensityComfortable;
        LabelDensityCompact     = Strings.DensityCompact;
        LabelSectionTheme       = Strings.SettingsSectionTheme;
        ThemeNote               = Strings.SettingsThemeNote;
        RefreshThemeCards();
    }

    /// <summary>The three cards' captions, the one in use saying so: Windows' high contrast wins over the theme.</summary>
    private void RefreshThemeCards()
    {
        ThemeInUse = ToolWindow.VsThemeDetector.HighContrastOn() ? "highContrast" : IsDarkTheme ? "dark" : "light";
        SegmentSelectedBg = IsDarkTheme ? "#2E2B3D" : "#E4DFF7";
        ThemeLightCaption        = Caption(Strings.ThemeLight,        "light");
        ThemeDarkCaption         = Caption(Strings.ThemeDark,         "dark");
        ThemeHighContrastCaption = Caption(Strings.ThemeHighContrast, "highContrast");

        string Caption(string name, string key) => key == ThemeInUse ? Strings.ThemeInUse(name) : name;
    }
}
