using System.IO;
using System.Runtime.Serialization;
using System.Threading;
using Inferpal.Localization;
using Inferpal.Services.Presentation;
using Inferpal.Services.VsIntegration;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>
/// The diagram of a closed ```mermaid fence: drawn by <see cref="MermaidRenderer"/>, its code one click away.
/// </summary>
/// <remarks>
/// ⚠ An object of its own, held only by a Mermaid block (<see cref="MarkdownBlock.Diagram"/>, null elsewhere): every
/// property and command of a block crosses to Visual Studio for every block of every message, and an ordinary block must
/// not carry the diagram's.
/// </remarks>
[DataContract]
internal sealed class MermaidDiagram : NotifyPropertyChangedObject
{
    private readonly string _code;
    /// <summary>"drawing", "drawn", "broken" (Mermaid could not read it) or "unavailable" (nothing to draw it with).</summary>
    private string _state = "drawing";
    private bool _codeChosen;
    private string _path = "";
    private double _width;
    private string _note = "";
    private string _reason = "";
    private (MermaidTheme, string)? _drawnIn;

    public MermaidDiagram(string code)
    {
        _code = code;
        ShowDiagramCommand = new AsyncCommand((_, _) => { CodeChosen = false; return Task.CompletedTask; });
        ShowCodeCommand    = new AsyncCommand((_, _) => { CodeChosen = true;  return Task.CompletedTask; });
        OpenCommand        = new AsyncCommand(OpenAsync);
    }

    [DataMember] public string Path   { get => _path;   private set => SetProperty(ref _path, value); }
    /// <summary>The diagram's width as drawn, in device-independent pixels: the PNG holds twice as many.</summary>
    [DataMember] public double Width  { get => _width;  private set => SetProperty(ref _width, value); }
    /// <summary>Why the diagram is shown as code (its title), empty when it is not.</summary>
    [DataMember] public string Note   { get => _note;   private set => SetProperty(ref _note, value); }
    /// <summary>Mermaid's own reason, for the model to fix; empty when there is none.</summary>
    [DataMember] public string Reason { get => _reason; private set => SetProperty(ref _reason, value); }

    [DataMember] public bool ShowsDiagram => _state == "drawn" && !_codeChosen;
    [DataMember] public bool ShowsDrawing => _state == "drawing" && !_codeChosen;
    [DataMember] public bool ShowsCode    => _codeChosen || _state is "broken" or "unavailable";
    [DataMember] public bool ShowsSwitch  => _state is "drawing" or "drawn";
    [DataMember] public bool CanOpen      => _state == "drawn";
    [DataMember] public bool DiagramChosen => !_codeChosen;
    [DataMember] public bool HasNote      => _note.Length > 0;
    [DataMember] public bool HasReason    => _reason.Length > 0;

    [DataMember] public AsyncCommand ShowDiagramCommand { get; }
    [DataMember] public AsyncCommand ShowCodeCommand    { get; }
    [DataMember] public AsyncCommand OpenCommand        { get; }

    [DataMember] public string LabelDiagram  => Strings.MermaidDiagram;
    [DataMember] public string LabelCode     => Strings.MermaidCode;
    [DataMember] public string LabelCopyCode => Strings.MermaidCopy;
    [DataMember] public string LabelOpen     => Strings.MermaidOpen;
    [DataMember] public string LabelDrawing  => Strings.MermaidDrawing;

    /// <summary>Draws the diagram in the colours of the chat whose code ground is <paramref name="codeBg"/>: on the block
    /// just parsed, and again on a theme switch; nothing when it is already drawn in them.</summary>
    internal void Draw(string codeBg)
    {
        var palette = ThemePalette.WithCodeBg(codeBg);
        var theme = palette == ThemePalette.For(isDark: true, highContrast: true) ? MermaidTheme.Contrast
                  : palette == ThemePalette.For(isDark: false) ? MermaidTheme.Light
                  : MermaidTheme.Dark;
        if (_drawnIn == (theme, codeBg)) return;
        _drawnIn = (theme, codeBg);
        _ = DrawAsync(theme, codeBg);
    }

    private bool CodeChosen
    {
        set { _codeChosen = value; RaiseShown(); }
    }

    private void SetState(string state)
    {
        _state = state;
        RaiseShown();
    }

    private void RaiseShown()
    {
        foreach (var name in new[] { nameof(ShowsDiagram), nameof(ShowsDrawing), nameof(ShowsCode), nameof(ShowsSwitch),
                                     nameof(CanOpen), nameof(DiagramChosen), nameof(HasNote), nameof(HasReason) })
            RaiseNotifyPropertyChangedEvent(name);
    }

    private async Task DrawAsync(MermaidTheme theme, string ground)
    {
        try
        {
            if (_state != "drawn") SetState("drawing");
            var drawing = await MermaidRenderer.Shared.DrawAsync(_code, theme, ground, CancellationToken.None);
            if (_drawnIn != (theme, ground)) return; // the theme switched again meanwhile: that drawing is under way
            if (drawing.PngPath is { } png)
            {
                Path  = png;
                Width = drawing.Width;
                Note = Reason = "";
                SetState("drawn");
                return;
            }
            // Never an empty picture: the reason, and the code. Inferpal's own limits are said as they are, never as
            // Mermaid's; a passing unavailability is tried again at the next drawing of the chat.
            Note   = drawing.Unavailable ? Strings.MermaidUnavailable : drawing.Limit ?? Strings.MermaidBroken;
            Reason = drawing.Error is { } why ? Strings.MermaidWhy(why) : "";
            if (drawing.Unavailable) _drawnIn = null;
            SetState(drawing.Unavailable ? "unavailable" : "broken");
        }
        catch (Exception ex)
        {
            Services.Diagnostics.Swallow("MermaidDiagram.Draw", ex);
            _drawnIn = null;
            Note = Strings.MermaidUnavailable;
            SetState("unavailable");
        }
    }

    /// <summary>Opens the drawing in Windows' image viewer.</summary>
    private Task OpenAsync(object? _, CancellationToken ct)
    {
        if (_state != "drawn" || !File.Exists(Path)) return Task.CompletedTask;
        try
        {
            using var _p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Services.Diagnostics.Swallow("MermaidDiagram.Open", ex);
        }
        return Task.CompletedTask;
    }
}
