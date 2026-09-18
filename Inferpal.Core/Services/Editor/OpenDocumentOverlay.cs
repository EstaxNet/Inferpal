using System.Collections.Concurrent;
using System.IO;

namespace Inferpal.Services.Editor;

/// <summary>
/// In-memory mirror of the editor's open — possibly unsaved — documents, fed by LSP-style
/// <c>didOpen</c>/<c>didChange</c>/<c>didClose</c> notifications from the editor adapter.
/// File-reading services consult it before touching disk so the model sees dirty-buffer
/// content instead of the stale on-disk version.
/// </summary>
/// <remarks>
/// Keys are normalised to full paths and compared case-insensitively (Windows-style editors
/// report the same file with varying casing). Thread-safe: notifications arrive on RPC
/// dispatch threads while tools read from the agent loop.
/// </remarks>
internal sealed class OpenDocumentOverlay
{
    // Path case-folding is a property of the file system, not of the process — the reasoning that
    // used to live here now lives in Services/PathComparer.cs, because four other sites had each
    // re-derived it and two of them disagreed about macOS.
    private readonly ConcurrentDictionary<string, Entry> _docs = new(Services.PathComparer.Default);

    private readonly record struct Entry(string Text, bool Unsaved);

    /// <summary>Mirrors an opened/edited document (full-text sync).</summary>
    /// <param name="unsaved">Whether the buffer holds changes the disk does not. An adapter that does not
    /// say is taken to mean it does: the buffer then wins over the disk, as it always did.</param>
    public void Set(string path, string text, bool unsaved = true) =>
        _docs[Normalize(path)] = new Entry(text, unsaved);

    /// <summary>Drops a closed document; subsequent reads fall back to disk.</summary>
    public void Remove(string path) => _docs.TryRemove(Normalize(path), out _);

    /// <summary>Buffered content of <paramref name="path"/>, when the document is open.</summary>
    public bool TryGet(string path, out string text)
    {
        var found = _docs.TryGetValue(Normalize(path), out var entry);
        text = found ? entry.Text : string.Empty;
        return found;
    }

    /// <summary>
    /// Buffered content of <paramref name="path"/>, only when it holds changes the disk does not.
    /// </summary>
    /// <remarks>
    /// A saved open document is read from disk: a tool that just wrote the file is newer than the
    /// buffer, which the editor reloads only later — or never, for a file its watcher excludes.
    /// </remarks>
    public bool TryGetUnsaved(string path, out string text)
    {
        var found = _docs.TryGetValue(Normalize(path), out var entry) && entry.Unsaved;
        text = found ? entry.Text : string.Empty;
        return found;
    }

    /// <summary>Paths of the documents currently mirrored (i.e. open in the editor).</summary>
    public IReadOnlyList<string> Paths => [.. _docs.Keys];

    public int Count => _docs.Count;

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }   // invalid path chars — keep the raw key rather than throwing
    }
}
