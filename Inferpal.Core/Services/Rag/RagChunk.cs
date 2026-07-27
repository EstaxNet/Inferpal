using System.Text.Json.Serialization;

namespace Inferpal.Services.Rag;

/// <summary>
/// A semantically coherent excerpt of a source file used for RAG retrieval.
/// Metadata is persisted as JSON; the embedding vector is stored separately
/// in a companion binary file and re-attached at load time.
/// </summary>
internal class RagChunk
{
    /// <summary>Absolute path to the source file.</summary>
    [JsonPropertyName("path")]    public string  FilePath    { get; set; } = string.Empty;

    /// <summary>Path relative to the solution root — used for display.</summary>
    [JsonPropertyName("rel")]     public string  RelPath     { get; set; } = string.Empty;

    /// <summary>1-based start line of the chunk within <see cref="FilePath"/>.</summary>
    [JsonPropertyName("start")]   public int     StartLine   { get; set; }

    /// <summary>1-based end line of the chunk within <see cref="FilePath"/>.</summary>
    [JsonPropertyName("end")]     public int     EndLine     { get; set; }

    /// <summary>Raw text of the chunk (code or prose).</summary>
    [JsonPropertyName("content")] public string  Content     { get; set; } = string.Empty;

    /// <summary>MD5 hex of <see cref="Content"/> — used to skip re-embedding unchanged chunks.</summary>
    [JsonPropertyName("hash")]    public string  ContentHash { get; set; } = string.Empty;

    /// <summary>C# type name (class / interface / …) when the chunk represents a single type; otherwise <c>null</c>.</summary>
    [JsonPropertyName("type")]    public string? TypeName    { get; set; }

    // ── Runtime-only (not serialized to JSON) ─────────────────────────────────

    /// <summary>Embedding vector loaded from the companion binary file; <c>null</c> if not yet embedded.</summary>
    [JsonIgnore] public float[]? Embedding { get; set; }
}
