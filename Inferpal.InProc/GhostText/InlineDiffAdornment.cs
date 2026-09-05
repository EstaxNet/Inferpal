using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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
    private static readonly SolidColorBrush NoticeBg;
    private static readonly SolidColorBrush NoticeFg;
    static InlineDiffAdornment()
    {
        RemovedBg = new SolidColorBrush(Color.FromArgb(0x38, 0xC0, 0x39, 0x2B)); RemovedBg.Freeze();
        AddedBg   = new SolidColorBrush(Color.FromArgb(0x30, 0x2E, 0xA0, 0x43)); AddedBg.Freeze();
        AddedFg   = new SolidColorBrush(Color.FromArgb(0xD0, 0x9C, 0xDC, 0xA8)); AddedFg.Freeze();
        ButtonBg  = new SolidColorBrush(Color.FromArgb(0xE0, 0x2D, 0x2D, 0x30)); ButtonBg.Freeze();
        NoticeBg  = new SolidColorBrush(Color.FromArgb(0xF0, 0x3A, 0x30, 0x24)); NoticeBg.Freeze();
        NoticeFg  = new SolidColorBrush(Color.FromArgb(0xF0, 0xE8, 0xC0, 0x88)); NoticeFg.Freeze();
    }

    /// <summary>How long a notice stays up before it removes itself.</summary>
    private static readonly TimeSpan NoticeLifetime = TimeSpan.FromSeconds(8);

    private readonly IWpfTextView    _view;
    private readonly IAdornmentLayer _layer;

    private DiffPlan?           _plan;
    private ITextSnapshot?      _shownSnapshot;      // buffer state the plan positions refer to
    private int[]?              _hunkStartOffsets;   // char offset of each hunk's OldStart line
    private Action<int, bool>?  _onDecision;         // (hunk index, accepted)
    private Action<bool>?       _onDecideAll;        // accept-all / reject-all
    private HashSet<int>        _decided = [];
    private UIElement?          _notice;             // transient one-liner, independent of _plan
    private DispatcherTimer?    _noticeTimer;

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
        ForgetNotice();          // the layer wipe took the element: do not keep its trace
        _plan = null;
        _shownSnapshot = null;
        _onDecision = null;
        _onDecideAll = null;
    }
    // Notice

    /// <summary>
    /// Shows a single transient line at the top of the viewport, for the cases where the preview
    /// ends <b>without applying anything</b>. Ignores a null/empty text (a request written by an
    /// older host carries no notices - the preview keeps working, it just stays as mute as before).
    /// </summary>
    /// <remarks>
    /// This is the in-process component's only channel to the user, and a channel was needed: the
    /// three exits of <see cref="InlineDiffController"/> that apply nothing rendered exactly what an
    /// ignored command renders - a tick clicked with no effect, a keystroke that throws the rewrite
    /// away - and their only trace fell into the in-proc <c>Diagnostics</c> ring, which the
    /// <c>/diagnostics</c> command (out-of-process host) does not read. Nobody, the user included,
    /// could read the failure.
    ///
    /// It never fights the typist, which is the rule of this whole preview:
    /// <see cref="UIElement.IsHitTestVisible"/> false (it takes neither click nor focus), anchored to
    /// the viewport so never in the middle of the text, and it clears itself.
    /// </remarks>
    internal void ShowNotice(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        DismissNotice();

        var ff       = _view.FormattedLineSource;
        var fontSize = ff?.DefaultTextProperties.FontRenderingEmSize ?? 13.0;

        var border = new Border
        {
            Background       = NoticeBg,
            CornerRadius     = new CornerRadius(3),
            Padding          = new Thickness(10, 3, 10, 4),
            IsHitTestVisible = false,
            Child            = new TextBlock
            {
                Text         = text,
                Foreground   = NoticeFg,
                FontSize     = fontSize,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth     = Math.Max(200, _view.ViewportWidth - 60),
            },
        };
        border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        Canvas.SetLeft(border, Math.Max(_view.ViewportLeft,
            _view.ViewportRight - border.DesiredSize.Width - 20));
        Canvas.SetTop(border, _view.ViewportTop + 4);
        _layer.AddAdornment(AdornmentPositioningBehavior.ViewportRelative, null, null, border, null);
        _notice = border;

        _noticeTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, _view.VisualElement.Dispatcher)
        {
            Interval = NoticeLifetime,
        };
        _noticeTimer.Tick += (_, _) => DismissNotice();
        _noticeTimer.Start();
    }

    /// <summary>Removes the notice if one is up. Idempotent.</summary>
    internal void DismissNotice()
    {
        var notice = _notice;
        ForgetNotice();
        if (notice is not null) _layer.RemoveAdornment(notice);
    }

    /// <summary>Drops the bookkeeping without touching the layer - for the two paths that have just
    /// called <c>RemoveAllAdornments</c>: the element is already gone, and keeping its reference
    /// would, on the next notice, remove an element belonging to the following preview.</summary>
    private void ForgetNotice()
    {
        _noticeTimer?.Stop();
        _noticeTimer = null;
        _notice      = null;
    }


    // ── Rendering ─────────────────────────────────────────────────────────────

    private void Repaint()
    {
        _layer.RemoveAllAdornments();
        ForgetNotice();
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
