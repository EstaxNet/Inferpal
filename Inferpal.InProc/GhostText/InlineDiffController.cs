using System.IO;
using System.Windows.Threading;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Signals;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

#pragma warning disable VSTHRD001   // Dispatcher.InvokeAsync is the correct in-proc mechanism (see GhostTextController)

namespace Inferpal.GhostText;

/// <summary>
/// Per-editor-view controller of the inline diff preview. Polls <see cref="InlineDiffPreviewSignal"/>
/// for a request targeting this view's document; when the buffer still matches the request's old
/// text, claims it (ack), plans the hunks (<see cref="InlineDiffPlanner"/>) and shows the
/// <see cref="InlineDiffAdornment"/>. Accepted hunks are applied to the buffer in a single edit
/// (one native undo unit). Any user edit while the preview is up dismisses it (reject-all) —
/// the preview must never fight the typist.
///
/// Lifetime: created in <see cref="GhostTextViewListener.TextViewCreated"/>; destroyed when the
/// view fires <c>Closed</c>.
/// </summary>
internal sealed class InlineDiffController
{
    private const int PollMs = 500;

    private readonly IWpfTextView        _view;
    private readonly InlineDiffAdornment _adornment;
    private readonly DispatcherTimer     _poll;
    private readonly string?             _filePath;

    private DiffPlan?     _plan;
    private HashSet<int>  _accepted = [];
    private bool          _applying;   // our own buffer edit must not be read as a user edit

    internal InlineDiffController(IWpfTextView view)
    {
        _view      = view;
        _adornment = new InlineDiffAdornment(view);
        _filePath  = TryGetFilePath(view);

        _poll = new DispatcherTimer(DispatcherPriority.ApplicationIdle, view.VisualElement.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(PollMs),
        };
        _poll.Tick += (_, _) => PollForRequest();
        if (_filePath is not null) _poll.Start();

        view.TextBuffer.Changed += OnTextChanged;
        view.Closed             += OnViewClosed;
    }

    // ── Request pickup ────────────────────────────────────────────────────────

    private void PollForRequest()
    {
        if (_adornment.IsActive || _filePath is null) return;

        var request = InlineDiffPreviewSignal.TryReadRequestFor(_filePath);
        if (request is null) return;

        // Freshness gate: the preview positions only make sense over the exact text the code
        // action rewrote. A drifted buffer discards the request — the host's pickup wait then
        // times out and it falls back to its usual direct apply (which fails safely on drift).
        if (_view.TextBuffer.CurrentSnapshot.GetText() != request.OldText)
        {
            InlineDiffPreviewSignal.DiscardRequest();
            return;
        }

        var plan = InlineDiffPlanner.Plan(request.OldText, request.NewText);
        if (!plan.HasChanges)
        {
            InlineDiffPreviewSignal.DiscardRequest();
            return;
        }

        InlineDiffPreviewSignal.Acknowledge(request.Id);
        _plan     = plan;
        _accepted = [];
        _adornment.Show(plan, OnHunkDecision, OnDecideAll);
    }

    // ── Decisions ─────────────────────────────────────────────────────────────

    private void OnHunkDecision(int hunkIndex, bool accepted)
    {
        if (_plan is null) return;
        if (accepted) _accepted.Add(hunkIndex);
        if (_adornment.MarkDecided(hunkIndex))
            ApplyAndClose();
    }

    private void OnDecideAll(bool accepted)
    {
        if (_plan is null) return;
        if (accepted)
            foreach (var hunk in _plan.Hunks) _accepted.Add(hunk.Index);
        ApplyAndClose();
    }

    private void ApplyAndClose()
    {
        var plan = _plan;
        _plan = null;
        _adornment.Hide();
        if (plan is null || _accepted.Count == 0) return;

        var buffer = _view.TextBuffer;
        if (buffer.CurrentSnapshot.GetText() != plan.OldText) return;   // drifted since shown

        var merged = InlineDiffPlanner.Apply(plan, _accepted);

        // Minimal single replacement (common prefix/suffix trimmed) so the caret and folds
        // outside the edited region stay put, and the whole decision is one Ctrl+Z step.
        var (start, oldLen, newSlice) = MinimalSpan(plan.OldText, merged);
        _applying = true;
        try { buffer.Replace(new Span(start, oldLen), newSlice); }
        catch (Exception ex) { Services.Diagnostics.Swallow("InlineDiffController.Apply", ex); }
        finally { _applying = false; }
    }

    /// <summary>Smallest replacement turning <paramref name="oldText"/> into <paramref name="newText"/>.</summary>
    private static (int Start, int OldLength, string NewSlice) MinimalSpan(string oldText, string newText)
    {
        var prefix = 0;
        var maxPrefix = Math.Min(oldText.Length, newText.Length);
        while (prefix < maxPrefix && oldText[prefix] == newText[prefix]) prefix++;

        var suffix = 0;
        var maxSuffix = Math.Min(oldText.Length, newText.Length) - prefix;
        while (suffix < maxSuffix &&
               oldText[oldText.Length - 1 - suffix] == newText[newText.Length - 1 - suffix]) suffix++;

        return (prefix, oldText.Length - prefix - suffix,
                newText[prefix..(newText.Length - suffix)]);
    }

    // ── Lifetime ──────────────────────────────────────────────────────────────

    private void OnTextChanged(object? sender, TextContentChangedEventArgs e)
    {
        // The user typed while deciding: the plan's positions are void — reject what's left.
        if (!_applying && _adornment.IsActive)
        {
            _plan = null;
            _ = _view.VisualElement.Dispatcher.InvokeAsync(() => _adornment.Hide());
        }
    }

    private void OnViewClosed(object? sender, EventArgs e)
    {
        _poll.Stop();
        _view.TextBuffer.Changed -= OnTextChanged;
        _view.Closed             -= OnViewClosed;
        _adornment.Hide();
    }

    private static string? TryGetFilePath(IWpfTextView view)
    {
        try
        {
            return view.TextBuffer.Properties.TryGetProperty<ITextDocument>(
                       typeof(ITextDocument), out var doc) && !string.IsNullOrEmpty(doc.FilePath)
                ? Path.GetFullPath(doc.FilePath)
                : null;
        }
        catch { return null; }
    }
}
