using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;

namespace Inferpal.Services.VsIntegration;

/// <summary>
/// Shared bridge between VS editor events and tool implementations.
/// </summary>
/// <remarks>
/// Holds the latest VS client context and text view snapshot (set by editor listeners),
/// tracks which files are currently open (ref-counted across tabs), and relays
/// prompts initiated from the editor context menu to the chat ViewModel.
/// All members are safe to call from any thread.
/// </remarks>
internal class VsContextHolder
{
    private readonly Dictionary<string, int> _openCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>Current VS client context; set by <c>ActiveDocumentTracker</c> on each editor activation.</summary>
    public IClientContext? Context { get; set; }

    private ITextViewSnapshot? _latestView;
    private volatile string    _activeFilePath = string.Empty;

    /// <summary>
    /// Fired when the active file path changes (different document gains focus).
    /// The event arg is the new file path.
    /// </summary>
    public event EventHandler<string>? ActiveFileChanged;

    /// <summary>Snapshot of the active text view; used by <c>get_active_document</c> and cursor/selection tools.</summary>
    public ITextViewSnapshot? LatestView
    {
        get => _latestView;
        set
        {
            // Check-and-set under the lock: concurrent open+changed activations could interleave
            // the test and the write (duplicate or out-of-order ActiveFileChanged), despite the
            // "safe from any thread" contract of the class doc (pre-1.6.0 architecture review). The
            // event itself fires outside the lock.
            string? changed = null;
            lock (_lock)
            {
                _latestView = value;
                if (value is not null)
                {
                    var path = value.Document.Uri.LocalPath ?? string.Empty;
                    if (path != _activeFilePath)
                    {
                        _activeFilePath = path;
                        changed = path;
                    }
                }
            }
            if (changed is not null) ActiveFileChanged?.Invoke(this, changed);
        }
    }

    // ── Pending prompt (editor context menu → chat window) ─────────────────

    /// <summary>
    /// A prompt sent from the editor context menu, with the model and attachment that go with it. Published and
    /// consumed as ONE value: four fields taken one by one let two actions launched in quick succession mix —
    /// the first prompt with the second action's model, and the second action sent with a null model.
    /// </summary>
    internal sealed record PendingPrompt(string Prompt, string? Model, string? AttachLabel, string? AttachContent);

    private PendingPrompt? _pending;
    public event EventHandler? PendingPromptAvailable;

    /// <summary>
    /// Sets a pending prompt to be consumed by the chat window.
    /// <para>
    /// <paramref name="modelOverride"/> semantics:
    /// <list type="bullet">
    ///   <item><c>null</c>  — normal chat; tools stay as the user configured them.</item>
    ///   <item><c>""</c>    — code action with no specific model (use DefaultModel, tools disabled).</item>
    ///   <item>non-empty   — code action using this specific model (tools disabled).</item>
    /// </list>
    /// </para>
    /// <para>
    /// When <paramref name="attachLabel"/> and <paramref name="attachContent"/> are provided the
    /// chat window will attach the code as a file chip rather than embedding it in the prompt text.
    /// </para>
    /// </summary>
    public void SetPendingPrompt(string prompt, string? modelOverride = null,
        string? attachLabel = null, string? attachContent = null)
    {
        // Keep empty string distinct from null: "" means "code action, use DefaultModel, no tools".
        System.Threading.Interlocked.Exchange(ref _pending,
            new PendingPrompt(prompt, modelOverride, attachLabel, attachContent));
        PendingPromptAvailable?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes the pending prompt and everything that goes with it in one step; null when there is none.</summary>
    public PendingPrompt? ConsumePending() =>
        System.Threading.Interlocked.Exchange(ref _pending, null);

    public void RegisterOpen(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        lock (_lock)
            _openCounts[path] = _openCounts.TryGetValue(path, out var c) ? c + 1 : 1;
    }

    public void RegisterClose(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        lock (_lock)
        {
            if (_openCounts.TryGetValue(path, out var c))
            {
                if (c <= 1) _openCounts.Remove(path);
                else        _openCounts[path] = c - 1;
            }
        }
    }

    public IReadOnlyList<string> GetOpenPaths()
    {
        lock (_lock)
            return [.. _openCounts.Keys];
    }
}
