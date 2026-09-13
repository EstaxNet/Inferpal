using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace Inferpal.GhostText;

/// <summary>
/// Manages the WPF TextBlocks that render ghost text on the adornment layer.
/// All methods must be called on the WPF dispatcher thread.
/// </summary>
internal sealed class GhostTextAdornment
{
    internal const string LayerName = "InferpalGhostText";

    // Created once and frozen so each Render() call reuses the same object.
    private static readonly SolidColorBrush GhostBrush;
    static GhostTextAdornment()
    {
        GhostBrush = new SolidColorBrush(Color.FromArgb(0x70, 0xB8, 0xB8, 0xB8));
        GhostBrush.Freeze();
    }

    private readonly IWpfTextView    _view;
    private readonly IAdornmentLayer _layer;

    // The completion shown and the caret position it was computed for.
    private string?        _pending;
    private SnapshotPoint  _anchor;

    /// <summary>
    /// The completion Tab would insert — none once the buffer has moved past the snapshot it was
    /// computed for (an edit made from another view of the same buffer moves no caret here).
    /// </summary>
    internal string? PendingCompletion =>
        _pending is not null && ReferenceEquals(_anchor.Snapshot, _view.TextBuffer.CurrentSnapshot) ? _pending : null;

    internal GhostTextAdornment(IWpfTextView view)
    {
        _view  = view;
        _layer = view.GetAdornmentLayer(LayerName);
    }

    /// <summary>Shows <paramref name="completion"/> at <paramref name="anchor"/>, replacing any previous one.</summary>
    /// <remarks>
    /// Replaced, never appended: the sidecar returns whole completions, and appending to one left over
    /// from an older snapshot produced a doubled text that Tab then inserted.
    /// </remarks>
    internal void Show(string completion, SnapshotPoint anchor)
    {
        _anchor  = anchor;
        _pending = completion;
        Repaint();
    }

    /// <summary>Removes all ghost-text adornments and clears pending state.</summary>
    internal void Hide()
    {
        _layer.RemoveAllAdornments();
        _pending = null;
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    private void Repaint()
    {
        _layer.RemoveAllAdornments();
        if (string.IsNullOrEmpty(_pending)) return;

        // Guard: anchor might have become invalid if the snapshot rolled forward.
        if (!ReferenceEquals(_anchor.Snapshot, _view.TextBuffer.CurrentSnapshot)) return;

        var caretLine = _view.GetTextViewLineContainingBufferPosition(_anchor);
        if (caretLine is null) return;

        var bounds   = caretLine.GetCharacterBounds(_anchor);
        var ff       = _view.FormattedLineSource;
        var family   = ff?.DefaultTextProperties.Typeface.FontFamily ?? new System.Windows.Media.FontFamily("Consolas");
        var fontSize = ff?.DefaultTextProperties.FontRenderingEmSize  ?? 13.0;
        var brush    = GhostBrush;

        var lines    = _pending!.Split('\n');
        double top   = bounds.Top;

        for (int i = 0; i < lines.Length; i++)
        {
            // Skip a trailing empty line produced by a trailing \n
            if (i == lines.Length - 1 && lines[i].Length == 0) break;

            var block = new TextBlock
            {
                Text             = lines[i],
                Foreground       = brush,
                FontFamily       = family,
                FontSize         = fontSize,
                IsHitTestVisible = false,
            };

            // First line: render at the caret X. Continuation lines: indented from viewport left.
            double left = i == 0 ? bounds.Left : _view.ViewportLeft + 20;
            Canvas.SetLeft(block, left);
            Canvas.SetTop (block, top);

            _layer.AddAdornment(
                behavior:        AdornmentPositioningBehavior.TextRelative,
                visualSpan:      new SnapshotSpan(_anchor, 0),
                tag:             null,
                adornment:       block,
                removedCallback: null);

            top += bounds.Height;
        }
    }

}
