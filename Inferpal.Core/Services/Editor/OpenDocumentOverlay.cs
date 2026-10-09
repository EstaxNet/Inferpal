using System.Collections.Concurrent;
using System.IO;

namespace Inferpal.Services.Editor;

/// <summary>A document the editor holds with changes the disk does not have; <c>null</c> text when the editor could not
/// give it (a designer, not a text buffer) — it is still unsaved.</summary>
internal readonly record struct UnsavedDocument(string Path, string? Text);

/// <summary>
/// In-memory mirror of the editor's open — possibly unsaved — documents. File-reading services consult it before
/// touching disk so the model sees dirty-buffer content instead of the stale on-disk version, and the writing tools
/// refuse a file whose buffer holds changes the disk does not.
/// </summary>
/// <remarks>
/// <para>
/// Two ways to feed it. <b>Pushed</b> (VS Code): LSP-style <c>didOpen</c>/<c>didChange</c>/<c>didClose</c>
/// notifications from the editor adapter, so it is current at every moment. <b>Pulled</b> (Visual Studio): a source
/// the overlay asks, at <see cref="RefreshAsync"/>, which documents hold unsaved changes NOW — the tool registry asks
/// before every tool call, and a writing tool asks again after its approval.
/// ⚠ Pulled, never mirrored from events there: a mirror that missed one save would keep calling a saved file
/// "unsaved", and refuse every legitimate write to it until the next keystroke.
/// </para>
/// <para>
/// Keys are normalised to full paths and compared like the file system compares them. Thread-safe: notifications
/// arrive on RPC dispatch threads while tools read from the agent loop, several at once in a parallel batch.
/// </para>
/// </remarks>
internal sealed class OpenDocumentOverlay
{
    // Path case-folding is a property of the file system, not of the process. The reasoning, and
    // the one answer for the whole repository, live in Services/PathComparer.cs.
    private readonly ConcurrentDictionary<string, Entry> _docs = new(Services.PathComparer.Default);

    private readonly record struct Entry(string? Text, bool Unsaved);

    private readonly Func<CancellationToken, Task<IReadOnlyList<UnsavedDocument>>>? _source;
    private readonly object _refreshGate = new();
    private Task? _refreshing;

    /// <summary>An overlay the editor pushes its documents into.</summary>
    public OpenDocumentOverlay() { }

    /// <summary>An overlay that asks <paramref name="source"/> which documents hold unsaved changes, at each
    /// <see cref="RefreshAsync"/>, and holds exactly those.</summary>
    public OpenDocumentOverlay(Func<CancellationToken, Task<IReadOnlyList<UnsavedDocument>>> source) => _source = source;

    /// <summary>How long a pulled refresh may take before the overlay keeps what it knew.</summary>
    internal static readonly TimeSpan RefreshBudget = TimeSpan.FromSeconds(3);

    /// <summary>Mirrors an opened/edited document (full-text sync).</summary>
    /// <param name="unsaved">Whether the buffer holds changes the disk does not. An adapter that does not
    /// say is taken to mean it does: the buffer then wins over the disk, as it always did.</param>
    public void Set(string path, string text, bool unsaved = true) =>
        _docs[Normalize(path)] = new Entry(text, unsaved);

    /// <summary>Drops a closed document; subsequent reads fall back to disk.</summary>
    public void Remove(string path) => _docs.TryRemove(Normalize(path), out _);

    /// <summary>
    /// Asks the editor, when it is one that answers on demand, which documents hold unsaved changes now; nothing for
    /// an editor that pushes its changes. Never throws but for the caller's cancellation.
    /// </summary>
    /// <remarks>
    /// ⚠ An editor that does not answer within <see cref="RefreshBudget"/> has not answered "nothing is unsaved": the
    /// overlay keeps what it last knew, and says once in /diagnostics that it could not ask.
    /// Concurrent callers (a parallel batch of reads) share one question.
    /// </remarks>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (_source is null) return;

        Task refresh;
        lock (_refreshGate)
        {
            if (_refreshing is null || _refreshing.IsCompleted) _refreshing = PullAsync();
            refresh = _refreshing;
        }
        await refresh.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task PullAsync()
    {
        const string context = "OpenDocumentOverlay";
        IReadOnlyList<UnsavedDocument> unsaved;
        try
        {
            using var budget = new CancellationTokenSource(RefreshBudget);
            unsaved = await _source!(budget.Token).WaitAsync(RefreshBudget).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _lastFailure = ex.GetType().Name;
            Diagnostics.RecordOnce(context,
                $"The editor did not say which files have unsaved changes ({ex.GetType().Name}: {ex.Message}). The last " +
                "answer stands: a file changed since then could be written over.",
                _lastFailure);
            return;
        }

        var keep = new HashSet<string>(Services.PathComparer.Default);
        foreach (var doc in unsaved)
        {
            var key = Normalize(doc.Path);
            keep.Add(key);
            _docs[key] = new Entry(doc.Text, Unsaved: true);
        }
        foreach (var key in _docs.Keys)
            if (!keep.Contains(key)) _docs.TryRemove(key, out _);

        // The editor answers again: the next time it does not is said again (the key does not move with the condition).
        if (_lastFailure is { } said) Diagnostics.Forget(context, said);
        _lastFailure = null;
    }

    /// <summary>The kind of the last failed pull — one at a time, the refreshes being shared.</summary>
    private string? _lastFailure;

    /// <summary>Buffered content of <paramref name="path"/>, when the document is open.</summary>
    public bool TryGet(string path, out string text)
    {
        var found = _docs.TryGetValue(Normalize(path), out var entry) && entry.Text is not null;
        text = found ? entry.Text! : string.Empty;
        return found;
    }

    /// <summary>Whether the editor holds changes to <paramref name="path"/> that the disk does not — as of the last
    /// notification, or the last <see cref="RefreshAsync"/>.</summary>
    public bool HasUnsavedChanges(string path) =>
        _docs.TryGetValue(Normalize(path), out var entry) && entry.Unsaved;

    /// <summary>
    /// Buffered content of <paramref name="path"/>, only when it holds changes the disk does not and the editor gave
    /// its text.
    /// </summary>
    /// <remarks>
    /// A saved open document is read from disk: a tool that just wrote the file is newer than the
    /// buffer, which the editor reloads only later — or never, for a file its watcher excludes.
    /// </remarks>
    public bool TryGetUnsaved(string path, out string text)
    {
        var found = _docs.TryGetValue(Normalize(path), out var entry) && entry.Unsaved && entry.Text is not null;
        text = found ? entry.Text! : string.Empty;
        return found;
    }

    /// <summary>Paths of the documents currently mirrored: every open document when the editor pushes them, the
    /// unsaved ones only when the overlay pulls.</summary>
    public IReadOnlyList<string> Paths => [.. _docs.Keys];

    public int Count => _docs.Count;

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }   // invalid path chars — keep the raw key rather than throwing
    }
}
