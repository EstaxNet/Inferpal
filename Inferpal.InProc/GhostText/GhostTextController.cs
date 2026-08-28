using System.Windows.Input;
using System.Windows.Threading;
using Inferpal.Services.CodeActions;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

// This file uses WPF Dispatcher.InvokeAsync throughout as the correct fire-and-forget
// mechanism for an in-process MEF component.  InvokeAsync is asynchronous (non-blocking),
// so the deadlock risk flagged by VSTHRD001 does not apply here.
#pragma warning disable VSTHRD001

namespace Inferpal.GhostText;

/// <summary>
/// Per-editor-view controller.  Wires up text-change debounce → FIM request → ghost-text
/// display, and intercepts Tab (accept) / Escape (dismiss) via WPF PreviewKeyDown.
///
/// Lifetime: created in <see cref="GhostTextViewListener.TextViewCreated"/>; destroyed
/// when the view fires <c>Closed</c>.
/// </summary>
internal sealed class GhostTextController
{
    private readonly IWpfTextView       _view;
    private readonly GhostTextAdornment _adornment;
    private readonly Dispatcher         _dispatcher;

    private readonly object              _gate = new();
    private Timer?                       _debounce;
    private CancellationTokenSource?     _cts;

    // Snapshot captured at trigger time — used to discard stale token batches
    // and to guard AcceptCompletion against buffer changes between trigger and Tab press.
    // Volatile: set on thread-pool (TriggerAsync), read on UI thread (AcceptCompletion).
    private volatile ITextSnapshot? _triggerSnapshot;

    internal GhostTextController(IWpfTextView view)
    {
        _view       = view;
        _adornment  = new GhostTextAdornment(view);
        _dispatcher = view.VisualElement.Dispatcher;

        view.TextBuffer.Changed           += OnTextChanged;
        view.Caret.PositionChanged        += OnCaretMoved;
        view.Closed                       += OnViewClosed;
        view.VisualElement.PreviewKeyDown += OnPreviewKeyDown;
    }

    // ── Text change → debounce ────────────────────────────────────────────────

    private void OnTextChanged(object? sender, TextContentChangedEventArgs e)
    {
        // Hide immediately on every keystroke — fire-and-forget (VSTHRD110: _ = discards result).
        _ = _dispatcher.InvokeAsync(() => _adornment.Hide());

        // Read the settings BEFORE acquiring the lock: the read may touch the disk, and must not
        // happen while holding _gate (avoids lock contention / potential deadlock).
        var config   = InProcConfig.Current();
        var settings = config.Enabled
            ? FimContextBuilder.GetSettings(config.Mode)
            : null;

        // Single lock covers the cancel→dispose→create sequence atomically, which
        // eliminates the window where the disposed timer's callback could fire and
        // call TriggerAsync() with a stale (already-cancelled) CancellationToken.
        lock (_gate)
        {
            _cts?.Cancel();
            _debounce?.Dispose();
            _triggerSnapshot = null; // snapshot is now stale
            _debounce = settings is not null
                ? new Timer(_ => _ = TriggerAsync(), null, settings.DebounceMs, Timeout.Infinite)
                : null;
        }
    }

    private void OnCaretMoved(object? sender, CaretPositionChangedEventArgs e)
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _triggerSnapshot = null;
        }
        _ = _dispatcher.InvokeAsync(() => _adornment.Hide());
    }

    // ── FIM request ───────────────────────────────────────────────────────────

    private async Task TriggerAsync()
    {
        CancellationToken token;
        CancellationTokenSource? prev;
        lock (_gate)
        {
            prev  = _cts;
            _cts  = new CancellationTokenSource();
            token = _cts.Token;
        }
        // Cancel outside the lock to avoid running callbacks while holding it.
        // (net472 has no CancelAsync(); the registered callback is a single Send on a pipe.)
        if (prev is not null)
            await Task.Run(() => prev.Cancel()).ConfigureAwait(false);

        try
        {
            // Read editor state on the dispatcher thread.
            var ctx = await _dispatcher.InvokeAsync(() => ReadContext(token));
            if (ctx is null || token.IsCancellationRequested) return;

            var (prefix, suffix, anchor, snapshot) = ctx.Value;
            _triggerSnapshot = snapshot;

            var config = InProcConfig.Current();
            if (!config.Enabled) return;
            var settings = FimContextBuilder.GetSettings(config.Mode);

            // Inference lives in the Core (net8), out of reach from this net472 assembly: it is
            // asked of the sidecar, which IS the Core. It returns the whole completion - streaming
            // token by token brought nothing here, the adornment only shows a complete text before
            // Tab can accept it anyway.
            var completion = await FimSidecar.CompleteAsync(
                prefix:      prefix,
                suffix:      suffix,
                maxTokens:   settings.MaxTokens,
                temperature: settings.Temperature,
                model:       config.Model,
                configStamp: config.Stamp,
                ct:          token).ConfigureAwait(false);

            if (completion is null || token.IsCancellationRequested) return;

            // Fire-and-forget UI dispatch — result intentionally discarded (VSTHRD110: _ =).
            _ = _dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested) return;
                // Discard if the buffer changed since we triggered.
                if (!ReferenceEquals(_view.TextBuffer.CurrentSnapshot, snapshot)) return;
                _adornment.Append(completion, anchor);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Services.Diagnostics.Swallow("GhostText.Trigger", ex); }
    }

    private (string prefix, string suffix, SnapshotPoint anchor, ITextSnapshot snapshot)?
        ReadContext(CancellationToken token)
    {
        if (token.IsCancellationRequested) return null;

        var snapshot = _view.TextBuffer.CurrentSnapshot;
        var caretPos = _view.Caret.Position.BufferPosition;
        var cursor   = caretPos.Position;
        var text     = snapshot.GetText();

        // Don't fire when IntelliSense trigger chars were just typed.
        if (cursor > 0 && IsIntelliSenseTrigger(text[cursor - 1])) return null;

        return (
            TailLines(text[..cursor], 64),
            HeadLines(text[cursor..], 16),
            caretPos,
            snapshot);
    }

    // ── Keyboard — Tab accepts, Escape dismisses ───────────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_adornment.PendingCompletion is not { } completion) return;

        if (e.Key == Key.Tab)
        {
            AcceptCompletion(completion);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _adornment.Hide();
            lock (_gate) { _cts?.Cancel(); }
            e.Handled = true;
        }
    }

    private void AcceptCompletion(string completion)
    {
        _adornment.Hide();
        lock (_gate) { _cts?.Cancel(); }

        try
        {
            // Guard: if the buffer changed since the completion was triggered, the insertion
            // position would be wrong — discard the stale completion instead of misplacing it.
            var triggered = _triggerSnapshot;
            if (triggered is not null && !ReferenceEquals(_view.TextBuffer.CurrentSnapshot, triggered))
                return;

            _triggerSnapshot = null; // consumed
            var pos = _view.Caret.Position.BufferPosition;
            using var edit = _view.TextBuffer.CreateEdit();
            edit.Insert(pos.Position, completion);
            var applied = edit.Apply();

            // Place caret at the end of the inserted text.
            var newPos = new SnapshotPoint(applied, pos.Position + completion.Length);
            _view.Caret.MoveTo(newPos);
        }
        catch (Exception ex) { Services.Diagnostics.Swallow("GhostText.AcceptCompletion", ex); }
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    private void OnViewClosed(object? sender, EventArgs e)
    {
        _view.TextBuffer.Changed          -= OnTextChanged;
        _view.Caret.PositionChanged       -= OnCaretMoved;
        _view.Closed                      -= OnViewClosed;
        _view.VisualElement.PreviewKeyDown -= OnPreviewKeyDown;

        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce        = null;
            _cts?.Cancel();
            _cts             = null;
            _triggerSnapshot = null;
        }

        _ = _dispatcher.InvokeAsync(() => _adornment.Hide());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly HashSet<char> IntelliSenseTriggers =
        ['.', '(', '[', '<', '"', '\'', ',', ' '];

    private static bool IsIntelliSenseTrigger(char c) => IntelliSenseTriggers.Contains(c);

    // Keeps the last `count` newline-delimited segments (= up to `count` lines before the cursor).
    private static string TailLines(string text, int count)
    {
        var span  = text.AsSpan();
        var found = 0;
        var idx   = span.Length;
        while (idx > 0 && found < count) { idx--; if (span[idx] == '\n') found++; }
        if (found == count && idx < span.Length && span[idx] == '\n') idx++;
        return text[idx..];
    }

    // Keeps the first `count` newline-delimited segments (= up to `count` lines after the cursor).
    private static string HeadLines(string text, int count)
    {
        var span  = text.AsSpan();
        var found = 0;
        var idx   = 0;
        while (idx < span.Length && found < count)
        {
            if (span[idx] == '\n') found++;
            idx++;
        }
        return text[..idx];
    }
}
