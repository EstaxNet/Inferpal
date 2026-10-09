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

    /// <summary>The VS client context of the latest Inferpal command (a copy frozen when it ran — see
    /// <see cref="ResolveActiveViewAsync"/>).</summary>
    public IClientContext? Context { get; set; }

    /// <summary>The chat window's X-Ray counts as they are now — what the settings' Context page shows. Set by the chat
    /// window; <c>null</c> while none exists (the page then says the conversation is counted once it opens).</summary>
    public Func<Task<Services.Presentation.XRayPanelModel?>>? ConversationUsage { get; set; }

    /// <summary>The repository's instruction files and what the chat's next question sends of each — the settings'
    /// Context page shows them. Set by the chat window; <c>null</c> while none exists.</summary>
    public Func<Task<IReadOnlyList<Services.Presentation.RepoInstructionRow>>>? RepoInstructions { get; set; }

    /// <summary>Opens the chat window's X-Ray panel — the settings' "Open Context X-Ray". Set by the chat window.</summary>
    public Func<Task>? OpenXray { get; set; }

    /// <summary>
    /// Asks an approval as a card in the chat, while a turn runs there; answers <c>null</c> when no turn is running,
    /// and the approval then keeps its dialog. Set by the chat window. The second argument opens the full diff (the
    /// dialog), <c>null</c> when the approval has none; its answer, when it gives one, answers the card.
    /// </summary>
    public Func<Services.Presentation.ApprovalCardModel, Func<CancellationToken, Task<ApprovalDecision?>>?, CancellationToken,
                Task<ApprovalDecision?>>? InlineApproval { get; set; }

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
        set => Activate(value, value?.Document.Uri.LocalPath);
    }

    /// <summary>The active document's path; empty when none is known.</summary>
    public string ActiveFilePath => _activeFilePath;

    /// <summary>Records <paramref name="view"/> as the latest view, of the document at <paramref name="path"/>.</summary>
    internal void Activate(ITextViewSnapshot? view, string? path)
    {
        // Check-and-set under the lock: concurrent open+changed activations could interleave
        // the test and the write (duplicate or out-of-order ActiveFileChanged), despite the
        // "safe from any thread" contract of the class doc. The
        // event itself fires outside the lock.
        string? changed = null;
        lock (_lock)
        {
            _latestView = view;
            if (path is not null && path != _activeFilePath)
            {
                _activeFilePath = path;
                changed = path;
            }
        }
        if (changed is not null) ActiveFileChanged?.Invoke(this, changed);
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
        {
            _openCounts[path] = _openCounts.TryGetValue(path, out var c) ? c + 1 : 1;
            _closed.Remove(path);
        }
    }

    /// <summary>Documents whose last view closed since they were seen open.</summary>
    private readonly HashSet<string> _closed = new(PathComparer.Default);

    /// <summary>
    /// A view closed. When it was the last view of the active document, that document stops being the active one.
    /// </summary>
    /// <remarks>
    /// ⚠ Kept, the closed document stayed "active": the attach button, /explain and /doc took it, the welcome screen named
    /// it, get_open_editors called it active while leaving it out of its own open list, and an agent edit went to a
    /// document no longer open.
    /// </remarks>
    public void ViewClosed(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var cleared = false;
        lock (_lock)
        {
            if (_openCounts.TryGetValue(path, out var c) && c > 1)
            {
                _openCounts[path] = c - 1;
                return;
            }
            _openCounts.Remove(path);
            _closed.Add(path);
            if (string.Equals(path, _activeFilePath, PathComparer.Comparison))
            {
                _latestView     = null;
                _activeFilePath = string.Empty;
                cleared         = true;
            }
        }
        if (cleared) ActiveFileChanged?.Invoke(this, string.Empty);
    }

    /// <summary>The document was seen open, and its last view has closed since.</summary>
    public bool IsKnownClosed(string path)
    {
        lock (_lock) return _closed.Contains(path);
    }

    /// <summary>
    /// The view the user works in: the one that took the latest edit, caret move or opening — or, before any, the one the
    /// latest Inferpal command was run from. The ONE reader for the agent's editor tools and the chat window.
    /// </summary>
    /// <remarks>
    /// ⚠ <see cref="Context"/> is a copy frozen when an Inferpal command ran: the active view it resolves is the one of
    /// THAT moment, document version and selection included. Read first, it had the agent read and edit the file the chat
    /// was opened from while the user worked in another — and get_active_document contradict get_open_editors.
    /// </remarks>
    public async Task<ITextViewSnapshot?> ResolveActiveViewAsync(VisualStudioExtensibility vs, CancellationToken ct)
    {
        if (LatestView is { } latest) return latest;
        if (Context is not { } context) return null;
        var view = await vs.Editor().GetActiveTextViewAsync(context, ct);
        // The frozen context outlives the document it was taken on: a closed one is no active document.
        return view is not null && IsKnownClosed(view.Document.Uri.LocalPath) ? null : view;
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
