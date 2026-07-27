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
    private readonly ConcurrentDictionary<string, string> _docs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mirrors an opened/edited document (full-text sync).</summary>
    public void Set(string path, string text) => _docs[Normalize(path)] = text;

    /// <summary>Drops a closed document; subsequent reads fall back to disk.</summary>
    public void Remove(string path) => _docs.TryRemove(Normalize(path), out _);

    /// <summary>Buffered content of <paramref name="path"/>, when the document is open.</summary>
    public bool TryGet(string path, out string text) => _docs.TryGetValue(Normalize(path), out text!);

    /// <summary>Paths of the documents currently mirrored (i.e. open in the editor).</summary>
    public IReadOnlyList<string> Paths => [.. _docs.Keys];

    public int Count => _docs.Count;

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }   // invalid path chars — keep the raw key rather than throwing
    }
}
