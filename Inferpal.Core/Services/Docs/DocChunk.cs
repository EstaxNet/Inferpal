using System.Text.Json.Serialization;

namespace Inferpal.Services.Docs;

/// <summary>
/// A passage of external documentation used for semantic retrieval by <c>search_docs</c>.
/// Mirrors <see cref="Rag.RagChunk"/> but is keyed by documentation source + page URL
/// instead of a source-file path. Persisted in the global <c>docs.db</c> SQLite store.
/// </summary>
internal sealed class DocChunk
{
    /// <summary>Id of the owning <see cref="DocSite"/>.</summary>
    [JsonPropertyName("docId")]   public string  DocId     { get; set; } = string.Empty;

    /// <summary>Absolute URL of the page this chunk was extracted from.</summary>
    [JsonPropertyName("url")]     public string  Url       { get; set; } = string.Empty;

    /// <summary>The page's <c>&lt;title&gt;</c> (or URL when absent).</summary>
    [JsonPropertyName("page")]    public string  PageTitle { get; set; } = string.Empty;

    /// <summary>Best-effort section heading for the chunk (first non-empty line).</summary>
    [JsonPropertyName("heading")] public string? Heading   { get; set; }

    /// <summary>Raw prose text of the chunk.</summary>
    [JsonPropertyName("content")] public string  Content   { get; set; } = string.Empty;

    /// <summary>MD5 hex of <see cref="Content"/> — lets re-indexing skip unchanged passages.</summary>
    [JsonPropertyName("hash")]    public string  ContentHash { get; set; } = string.Empty;

    // ── Runtime-only ──────────────────────────────────────────────────────────

    /// <summary>Embedding vector; <c>null</c> when not yet embedded.</summary>
    [JsonIgnore] public float[]? Embedding { get; set; }
}
