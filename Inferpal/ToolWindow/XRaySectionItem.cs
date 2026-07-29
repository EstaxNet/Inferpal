using System.Globalization;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>
/// One row of the interactive Context X-Ray panel: a prompt layer with its token bar, an
/// on/off toggle (applied to the next turn) and an expandable exact-content preview.
/// Cross-boundary type → primitives only; colours are pushed as strings because the VS theme
/// does not propagate through nested DataTemplates.
/// </summary>
[DataContract]
internal sealed class XRaySectionItem : NotifyPropertyChangedObject
{
    private bool _enabled;
    private bool _isExpanded;

    /// <summary>Stable section id (<see cref="XRayPanelPresenter.SectionId"/>) — extension-process only.</summary>
    public string Id { get; }

    [DataMember] public string Label       { get; }
    [DataMember] public string TokensText  { get; }
    [DataMember] public double Percent     { get; }
    [DataMember] public string Content     { get; }
    [DataMember] public bool   CanToggle   { get; }
    [DataMember] public bool   Enabled     { get => _enabled;    set => SetProperty(ref _enabled,    value); }
    [DataMember] public bool   IsExpanded  { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }

    // Theme colours (set at construction; the panel is rebuilt on open so they stay current).
    [DataMember] public string ThemeText       { get; }
    [DataMember] public string ThemeSubtleText { get; }
    [DataMember] public string ThemeCodeBg     { get; }
    [DataMember] public string ThemeCodeBorder { get; }

    /// <summary>Flips <see cref="Enabled"/> and notifies the VM so the next turn's prompt follows.</summary>
    [DataMember] public AsyncCommand ToggleEnabledCommand { get; }
    /// <summary>Shows/hides the exact section content under the row.</summary>
    [DataMember] public AsyncCommand ToggleExpandCommand  { get; }

    public XRaySectionItem(XRaySectionModel model, ThemePalette palette, Action<XRaySectionItem> onToggled)
    {
        Id         = model.Id;
        Label      = model.Label;
        TokensText = "~" + model.Tokens.ToString("N0", CultureInfo.CurrentCulture);
        Percent    = model.Percent;
        Content    = model.Content;
        CanToggle  = model.CanToggle;
        _enabled   = model.Enabled;

        ThemeText       = palette.Text;
        ThemeSubtleText = palette.SubtleText;
        ThemeCodeBg     = palette.CodeBg;
        ThemeCodeBorder = palette.CodeBorder;

        ToggleEnabledCommand = new AsyncCommand((_, _) =>
        {
            if (CanToggle) { Enabled = !Enabled; onToggled(this); }
            return Task.CompletedTask;
        });
        ToggleExpandCommand = new AsyncCommand((_, _) => { IsExpanded = !IsExpanded; return Task.CompletedTask; });
    }
}
