using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Inferpal.Services.CodeActions;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace Inferpal.GhostText;

/// <summary>
/// Renders an inline diff preview on its adornment layer: red-tinted highlights over the lines a
/// hunk removes, green ghost lines for the text it adds, and a ✓/✗ button pair per hunk (plus a
/// ✓✓/✗✗ pair on the first hunk for accept-all/reject-all). Repaints on every layout change so
/// scrolling keeps the overlays anchored. All methods run on the WPF dispatcher thread.
/// Decisions are reported to the controller via the callbacks handed to <see cref="Show"/>;
/// this class never touches the text buffer.
/// </summary>
internal sealed class InlineDiffAdornment
{
    internal const string LayerName = "InferpalInlineDiff";

    private static readonly SolidColorBrush RemovedBg;
    private static readonly SolidColorBrush AddedBg;
    private static readonly SolidColorBrush AddedFg;
    private static readonly SolidColorBrush ButtonBg;
    static InlineDiffAdornment()
    {
        RemovedBg = new SolidColorBrush(Color.FromArgb(0x38, 0xC0, 0x39, 0x2B)); RemovedBg.Freeze();
        AddedBg   = new SolidColorBrush(Color.FromArgb(0x30, 0x2E, 0xA0, 0x43)); AddedBg.Freeze();
        AddedFg   = new SolidColorBrush(Color.FromArgb(0xD0, 0x9C, 0xDC, 0xA8)); AddedFg.Freeze();
        ButtonBg  = new SolidColorBrush(Color.FromArgb(0xE0, 0x2D, 0x2D, 0x30)); ButtonBg.Freeze();
    }

    private readonly IWpfTextView    _view;
    private readonly IAdornmentLayer _layer;

    private DiffPlan?           _plan;
    private ITextSnapshot?      _shownSnapshot;      // buffer state the plan positions refer to
    private int[]?              _hunkStartOffsets;   // char offset of each hunk's OldStart line
    private Action<int, bool>?  _onDecision;         // (hunk index, accepted)
    private Action<bool>?       _onDecideAll;        // accept-all / reject-all
    private HashSet<int>        _decided = [];

    internal bool IsActive => _plan is not null;

    internal InlineDiffAdornment(IWpfTextView view)
    {
        _view  = view;
        _layer = view.GetAdornmentLayer(LayerName);
        view.LayoutChanged += (_, _) => { if (IsActive) Repaint(); };
    }

    /// <summary>Starts showing <paramref name="plan"/> over the current buffer snapshot
    /// (whose text the controller has verified to equal <c>plan.OldText</c>).</summary>
    internal void Show(DiffPlan plan, Action<int, bool> onDecision, Action<bool> onDecideAll)
    {
        _plan          = plan;
        _shownSnapshot = _view.TextBuffer.CurrentSnapshot;
        _onDecision    = onDecision;
        _onDecideAll   = onDecideAll;
        _decided       = [];
        _hunkStartOffsets = ComputeHunkOffsets(plan);
        Repaint();
    }

    /// <summary>Marks a hunk as decided (its overlays disappear); returns true when all are.</summary>
    internal bool MarkDecided(int hunkIndex)
    {
        _decided.Add(hunkIndex);
        Repaint();
        return _plan is not null && _decided.Count >= _plan.Hunks.Count;
    }

    internal void Hide()
    {
        _layer.RemoveAllAdornments();
        _plan = null;
        _shownSnapshot = null;
        _onDecision = null;
        _onDecideAll = null;
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    private void Repaint()
    {
        _layer.RemoveAllAdornments();
        var plan = _plan;
        var snapshot = _shownSnapshot;
        if (plan is null || snapshot is null) return;

        // The preview is only valid over the exact snapshot it was planned against; the
        // controller hides it on any buffer change, this is just the last-resort guard.
        if (!ReferenceEquals(snapshot, _view.TextBuffer.CurrentSnapshot)) return;

        var ff       = _view.FormattedLineSource;
        var family   = ff?.DefaultTextProperties.Typeface.FontFamily ?? new FontFamily("Consolas");
        var fontSize = ff?.DefaultTextProperties.FontRenderingEmSize  ?? 13.0;

        var first = true;
        foreach (var hunk in plan.Hunks)
        {
            if (_decided.Contains(hunk.Index)) { first = false; continue; }

            var anchorOffset = _hunkStartOffsets![hunk.Index - 1];
            var anchor       = new SnapshotPoint(snapshot, Math.Min(anchorOffset, snapshot.Length));
            var anchorLine   = TryGetViewLine(anchor);
            if (anchorLine is null) { first = false; continue; }   // scrolled out of view

            // 1. Red-tinted highlight over each removed/replaced line still in view.
            for (var l = 0; l < hunk.OldLines.Count; l++)
            {
                var lineStart = snapshot.GetLineFromLineNumber(
                    Math.Min(snapshot.GetLineNumberFromPosition(anchor) + l, snapshot.LineCount - 1));
                var viewLine = TryGetViewLine(lineStart.Start);
                if (viewLine is null) continue;

                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width            = Math.Max(_view.ViewportWidth, viewLine.Width),
                    Height           = viewLine.Height,
                    Fill             = RemovedBg,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(rect, _view.ViewportLeft);
                Canvas.SetTop(rect, viewLine.Top);
                _layer.AddAdornment(AdornmentPositioningBehavior.TextRelative,
                    new SnapshotSpan(lineStart.Start, 0), null, rect, null);
            }

            // 2. Green ghost lines for the added text, below the removed block.
            var ghostTop = anchorLine.Top + hunk.OldLines.Count * anchorLine.Height;
            for (var l = 0; l < hunk.NewLines.Count; l++)
            {
                var block = new TextBlock
                {
                    Text             = hunk.NewLines[l],
                    Foreground       = AddedFg,
                    Background       = AddedBg,
                    FontFamily       = family,
                    FontSize         = fontSize,
                    MinWidth         = _view.ViewportWidth,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(block, _view.ViewportLeft);
                Canvas.SetTop(block, ghostTop + l * anchorLine.Height);
                _layer.AddAdornment(AdornmentPositioningBehavior.TextRelative,
                    new SnapshotSpan(anchor, 0), null, block, null);
            }

            // 3. ✓/✗ buttons at the hunk's first line (plus ✓✓/✗✗ on the first undecided hunk).
            var panel = BuildButtonPanel(hunk.Index, includeAllButtons: first, fontSize);
            Canvas.SetLeft(panel, _view.ViewportRight - 140);
            Canvas.SetTop(panel, anchorLine.Top);
            _layer.AddAdornment(AdornmentPositioningBehavior.TextRelative,
                new SnapshotSpan(anchor, 0), null, panel, null);

            first = false;
        }
    }

    private StackPanel BuildButtonPanel(int hunkIndex, bool includeAllButtons, double fontSize)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(MakeButton("✓", fontSize, Colors.LightGreen, () => _onDecision?.Invoke(hunkIndex, true)));
        panel.Children.Add(MakeButton("✗", fontSize, Colors.IndianRed,  () => _onDecision?.Invoke(hunkIndex, false)));
        if (includeAllButtons)
        {
            panel.Children.Add(MakeButton("✓✓", fontSize, Colors.LightGreen, () => _onDecideAll?.Invoke(true)));
            panel.Children.Add(MakeButton("✗✗", fontSize, Colors.IndianRed,  () => _onDecideAll?.Invoke(false)));
        }
        return panel;
    }

    private static Border MakeButton(string glyph, double fontSize, Color fg, Action onClick)
    {
        var border = new Border
        {
            Background      = ButtonBg,
            CornerRadius    = new CornerRadius(3),
            Margin          = new Thickness(2, 0, 0, 0),
            Padding         = new Thickness(6, 0, 6, 1),
            Cursor          = System.Windows.Input.Cursors.Hand,
            Child           = new TextBlock
            {
                Text       = glyph,
                Foreground = new SolidColorBrush(fg),
                FontSize   = fontSize,
            },
        };
        border.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
        return border;
    }

    private Microsoft.VisualStudio.Text.Formatting.IWpfTextViewLine? TryGetViewLine(SnapshotPoint point)
    {
        try { return _view.GetTextViewLineContainingBufferPosition(point); }
        catch { return null; }
    }

    /// <summary>Char offset of each hunk's first old line (hunks indexed 1..n).</summary>
    private static int[] ComputeHunkOffsets(DiffPlan plan)
    {
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < plan.OldText.Length; i++)
            if (plan.OldText[i] == '\n')
                lineStarts.Add(i + 1);

        var offsets = new int[plan.Hunks.Count];
        foreach (var hunk in plan.Hunks)
            offsets[hunk.Index - 1] = hunk.OldStart < lineStarts.Count
                ? lineStarts[hunk.OldStart]
                : plan.OldText.Length;
        return offsets;
    }
}
