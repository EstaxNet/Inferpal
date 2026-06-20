using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Inferpal.Localization;

namespace Inferpal.Commands;

/// <summary>
/// Minimal WPF dialog that collects the user's edit instruction, then stays open
/// as a spinner-overlay while the AI generates the response.
///
/// Lifecycle (driven by <see cref="InlineEditSelectionCommand"/>):
/// <list type="number">
///   <item>Create and show on a dedicated STA thread.</item>
///   <item>Caller awaits <see cref="InstructionTask"/> to get the typed instruction.</item>
///   <item>Caller calls <see cref="SwitchToLoading"/> to transform the window into a spinner.</item>
///   <item>When generation is complete (or failed), caller calls <see cref="CloseFromThread"/>.</item>
/// </list>
/// </summary>
internal sealed class InlineEditInputWindow : Window
{
    // ── Frozen brushes ─────────────────────────────────────────────────────────
    private static readonly SolidColorBrush _bg     = Freeze(new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26)));
    private static readonly SolidColorBrush _border = Freeze(new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)));
    private static readonly SolidColorBrush _accent = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC)));
    private static readonly SolidColorBrush _fg     = Freeze(new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)));
    private static readonly SolidColorBrush _dim    = Freeze(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)));

    private static T Freeze<T>(T b) where T : Freezable { b.Freeze(); return b; }

    // ── Spinner frames (braille spinner) ───────────────────────────────────────
    private static readonly string[] _frames = ["⣾", "⣽", "⣻", "⢿", "⡿", "⣟", "⣯", "⣷"];
    private int _frameIdx;

    // ── UI panels ──────────────────────────────────────────────────────────────
    private readonly StackPanel  _inputPanel;
    private readonly StackPanel  _loadingPanel;
    private readonly TextBlock   _spinnerBlock;
    private DispatcherTimer?     _spinnerTimer;

    // ── Async result ───────────────────────────────────────────────────────────
    private readonly TaskCompletionSource<string?> _instructionTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes when the user clicks "Apply" (with the instruction string) or
    /// cancels / closes the window (with <see langword="null"/>).
    /// </summary>
    public Task<string?> InstructionTask => _instructionTcs.Task;

    // When true the window opens straight into spinner mode (no instruction input):
    // used by the fixed-instruction code actions (Refactor / Fix / Add docs).
    private readonly bool _spinnerOnly;

    // ── Constructor ─────────────────────────────────────────────────────────────
    public InlineEditInputWindow(bool spinnerOnly = false)
    {
        _spinnerOnly = spinnerOnly;
        Title                 = Strings.InlineEditDlgTitle;
        SizeToContent         = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode            = ResizeMode.NoResize;
        MinWidth              = 480;
        Background            = _bg;
        BorderBrush           = _border;
        BorderThickness       = new Thickness(1);
        Topmost               = true;

        // ── Input panel ───────────────────────────────────────────────────────
        var header = new TextBlock
        {
            Text       = Strings.InlineEditDlgHeader,
            Foreground = _accent,
            FontSize   = 12,
            Margin     = new Thickness(0, 0, 0, 6),
        };

        var box = new TextBox
        {
            Width           = 450,
            Background      = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            Foreground      = _fg,
            CaretBrush      = _fg,
            SelectionBrush  = _accent,
            BorderBrush     = _accent,
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(6, 4, 6, 4),
            FontSize        = 13,
            MaxLength       = 600,
        };

        var hint = new TextBlock
        {
            Text       = Strings.InlineEditDlgHint,
            Foreground = _dim,
            FontSize   = 11,
            Margin     = new Thickness(0, 3, 0, 0),
        };

        var btnOk = new Button
        {
            Content         = Strings.BtnApply,
            Width           = 80,
            Height          = 26,
            IsDefault       = true,
            Background      = _accent,
            Foreground      = Brushes.White,
            BorderThickness = new Thickness(0),
            Margin          = new Thickness(0, 0, 8, 0),
        };
        var btnCancel = new Button
        {
            Content         = Strings.BtnCancel,
            Width           = 80,
            Height          = 26,
            IsCancel        = true,
            Background      = _border,
            Foreground      = _fg,
            BorderThickness = new Thickness(0),
        };

        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, 10, 0, 0),
        };
        btnRow.Children.Add(btnOk);
        btnRow.Children.Add(btnCancel);

        _inputPanel = new StackPanel { Orientation = Orientation.Vertical };
        _inputPanel.Children.Add(header);
        _inputPanel.Children.Add(box);
        _inputPanel.Children.Add(hint);
        _inputPanel.Children.Add(btnRow);

        // ── Loading panel ─────────────────────────────────────────────────────
        _spinnerBlock = new TextBlock
        {
            Text              = _frames[0],
            FontSize          = 20,
            Foreground        = _accent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 10, 0),
        };
        var workingText = new TextBlock
        {
            Text              = Strings.InlineEditWorking,
            Foreground        = _fg,
            FontSize          = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _loadingPanel = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(0, 12, 0, 12),
            Visibility          = Visibility.Collapsed,
        };
        _loadingPanel.Children.Add(_spinnerBlock);
        _loadingPanel.Children.Add(workingText);

        // ── Root layout ───────────────────────────────────────────────────────
        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(_inputPanel);
        root.Children.Add(_loadingPanel);

        Content = new Border
        {
            Background = _bg,
            Padding    = new Thickness(14, 10, 14, 12),
            Child      = root,
        };

        // ── Wiring ────────────────────────────────────────────────────────────
        btnOk.Click += (_, _) =>
        {
            var txt = box.Text.Trim();
            if (string.IsNullOrEmpty(txt)) return;
            // Signal instruction without closing — SwitchToLoading keeps the window alive.
            _instructionTcs.TrySetResult(txt);
            SwitchToLoadingCore();
        };

        btnCancel.Click += (_, _) =>
        {
            _instructionTcs.TrySetResult(null);
            Close();
        };

        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Return) return;
            e.Handled = true;
            var txt   = box.Text.Trim();
            if (string.IsNullOrEmpty(txt)) return;
            _instructionTcs.TrySetResult(txt);
            SwitchToLoadingCore();
        };

        // Handle X-button / Alt+F4 while in input mode
        Closing += (_, _) => _instructionTcs.TrySetResult(null);

        Loaded += (_, _) =>
        {
            Topmost = false;
            Activate();
            if (_spinnerOnly) SwitchToLoadingCore(); // no instruction to collect — spin immediately
            else              box.Focus();
        };
    }

    // ── Public API for the command (cross-thread safe) ─────────────────────────

    /// <summary>
    /// Switches the dialog to loading mode from any thread.
    /// (No-op if already switched.)
    /// </summary>
    public void SwitchToLoading()
    {
#pragma warning disable VSTHRD001, VSTHRD110 // Intentional: marshalling to WPF Dispatcher (STA), not VS JoinableTaskFactory
        _ = Dispatcher.InvokeAsync(SwitchToLoadingCore);
#pragma warning restore VSTHRD001, VSTHRD110
    }

    /// <summary>
    /// Closes the dialog from any thread.
    /// Safe to call even after the window is already closed.
    /// </summary>
    public void CloseFromThread()
    {
#pragma warning disable VSTHRD001, VSTHRD110 // Intentional: marshalling to WPF Dispatcher (STA), not VS JoinableTaskFactory
        _ = Dispatcher.InvokeAsync(() =>
        {
            _spinnerTimer?.Stop();
            try { Close(); } catch { /* already closed */ }
        });
#pragma warning restore VSTHRD001, VSTHRD110
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>Must be called on the STA/Dispatcher thread.</summary>
    private void SwitchToLoadingCore()
    {
        if (_loadingPanel.Visibility == Visibility.Visible) return; // idempotent

        _inputPanel.Visibility  = Visibility.Collapsed;
        _loadingPanel.Visibility = Visibility.Visible;
        SizeToContent            = SizeToContent.WidthAndHeight; // shrink/fit

        _spinnerTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(80),
        };
        _spinnerTimer.Tick += (_, _) =>
        {
            _frameIdx      = (_frameIdx + 1) % _frames.Length;
            _spinnerBlock.Text = _frames[_frameIdx];
        };
        _spinnerTimer.Start();
    }

    // ── Static factory ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates and shows the window on a dedicated STA thread.
    /// Returns a <see cref="Task{InlineEditInputWindow}"/> that completes as soon as the
    /// window finishes loading (i.e. is visible and ready for input).
    /// </summary>
    public static Task<InlineEditInputWindow> CreateAndShowAsync()
    {
        var tcs = new TaskCompletionSource<InlineEditInputWindow>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                var dlg = new InlineEditInputWindow();
                // Signal the caller once the window is rendered and interactive.
                dlg.Loaded += (_, _) => tcs.TrySetResult(dlg);
                dlg.ShowDialog();   // blocks until Close() is called
                // In case Loaded never fired (e.g. the window was closed before loading).
                tcs.TrySetResult(dlg);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Name         = "Inferpal-InlineEditDialog";
        thread.Start();

        return tcs.Task;
    }

    /// <summary>
    /// Creates and shows the window already in spinner mode (no instruction input), for
    /// the fixed-instruction code actions. The caller calls <see cref="CloseFromThread"/>
    /// once generation completes. <see cref="InstructionTask"/> is not used in this mode.
    /// </summary>
    public static Task<InlineEditInputWindow> CreateAndShowSpinnerAsync()
    {
        var tcs = new TaskCompletionSource<InlineEditInputWindow>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                var dlg = new InlineEditInputWindow(spinnerOnly: true);
                dlg.Loaded += (_, _) => tcs.TrySetResult(dlg);
                dlg.ShowDialog();
                tcs.TrySetResult(dlg);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Name         = "Inferpal-CodeActionSpinner";
        thread.Start();

        return tcs.Task;
    }
}
