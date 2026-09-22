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

    /// <summary>MD5 hex of <see cref="Content"/>, written and persisted.</summary>
    /// <remarks>
    /// ⚠ <b>Nothing compares it.</b> It said "lets re-indexing skip unchanged passages" by analogy
    /// with <see cref="Rag.RagChunk"/>, whose pass really does compare it — here a re-index re-embeds
    /// the whole site, and that is the expensive step that opens the circuit breaker and leaves the
    /// permanent hole this corpus is documented to keep. A comment promising an optimisation nobody
    /// wrote sends the next reader looking for the bug somewhere else.
    /// </remarks>
    [JsonPropertyName("hash")]    public string  ContentHash { get; set; } = string.Empty;

    // ── Runtime-only ──────────────────────────────────────────────────────────

    /// <summary>Embedding vector; <c>null</c> when not yet embedded.</summary>
    [JsonIgnore] public float[]? Embedding { get; set; }

    /// <summary>
    /// BM25 tokens of the passage — <b>content + page title + heading + URL</b> — computed on first
    /// use and cached for the chunk's lifetime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>What NAMES a passage is not in its prose.</b> Reference documentation puts the noun in
    /// the title, the heading and the URL, and then says "it" — so a body-only tokenization cannot
    /// find a page called <i>Retries</i> from the word <c>retries</c>, while <c>search_docs</c>
    /// renders every row as that same title and URL. The model is shown exactly the fields it
    /// cannot query. Same rule, and same reason, as <see cref="Rag.RagChunk.Bm25Tokens"/>
    /// (content + type name + relative path).
    /// </para>
    /// <para>
    /// ⚠ It compounds with the embedding hole this corpus keeps permanently: the lexical half is
    /// the ONLY route to a chunk held without a vector, so for those chunks a missing title was not
    /// weaker evidence — it was no route at all.
    /// </para>
    /// <para>
    /// The cache needs no eviction: a re-index always builds fresh <see cref="DocChunk"/> instances,
    /// in the chunker and when hydrating from SQLite. Benign race: two threads may tokenize
    /// concurrently, the results are identical and one reference wins.
    /// </para>
    /// </remarks>
    [JsonIgnore] public IReadOnlyList<string> Bm25Tokens =>
        _bm25Tokens ??= Rag.CodeTokenizer.Tokenize($"{Content}\n{PageTitle}\n{Heading}\n{Url}");

    private IReadOnlyList<string>? _bm25Tokens;
}
