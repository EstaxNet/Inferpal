using System.Text;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Rag;

namespace Inferpal.Services.Tools;

/// <summary>
/// Exposes semantic codebase search to the agentic loop.
/// Embeds the natural language query via Ollama and retrieves the most relevant code chunks.
/// Falls back to keyword search when the embedding model is unavailable.
/// </summary>
internal sealed class SemanticSearchTool : ITool
{
    private readonly ProjectIndexService _index;
    private readonly IInferenceProvider  _client;
    private readonly InferpalConfig   _config;

    public SemanticSearchTool(
        ProjectIndexService index,
        IInferenceProvider  client,
        InferpalConfig   config)
    {
        _index  = index;
        _client = client;
        _config = config;
    }

    public string Name => "search_codebase";

    public string Description =>
        "Semantically searches the indexed codebase for code snippets relevant to a natural language query. " +
        "Use this to find classes, methods, or patterns related to a concept across ALL project files " +
        "without knowing the exact file names. Returns ranked code chunks with file paths and line numbers. " +
        "Prefer this over search_in_files for conceptual or cross-file questions.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            query = new
            {
                type        = "string",
                description = "Natural language description of what code you are looking for " +
                              "(e.g. 'authentication logic', 'database connection handling', 'error retry pattern')."
            },
            top_k = new
            {
                type        = "integer",
                description = "Number of results to return (1–10). Defaults to the configured ragTopK setting."
            }
        },
        required = new[] { "query" }
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var query = args.GetProperty("query").GetString()?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(query))
            return "query is required.";

        var topK = args.TryGetProperty("top_k", out var tk)
            ? Math.Clamp(tk.GetInt32(), 1, 10)
            : Math.Max(1, _config.RagTopK);

        // ── Guard: index not ready ────────────────────────────────────────────
        if (_index.ChunkCount == 0)
        {
            var status = _index.IsIndexing ? _index.Status : "Index not built.";
            return Strings.RagIndexNotReady(status);
        }

        // ── Embed the query (shadow cache fast path) ──────────────────────────
        // If the user typed a prompt matching this query before sending, the embedding
        // and initial results were pre-computed in the background (0 ms round-trip).
        float[]? queryEmbedding = null;
        List<(RagChunk Chunk, float Score)>? cachedResults = null;

        if (_config.RagEnabled)
        {
            var model = string.IsNullOrEmpty(_config.RagEmbeddingModel)
                ? "nomic-embed-text"
                : _config.RagEmbeddingModel;

            // Try shadow cache first — free if query matches exactly
            var (shadowEmb, shadowRes) = _index.TryGetShadow(query);
            if (shadowEmb is not null)
            {
                queryEmbedding = shadowEmb;   // reuse pre-computed embedding
                cachedResults  = shadowRes;   // reuse pre-computed results if topK fits
            }
            else
            {
                queryEmbedding = await _client.GetEmbeddingAsync(query, model, ct);
            }
        }

        // ── Search (use shadow results when topK is satisfied) ────────────────
        List<(RagChunk Chunk, float Score)> results;
        if (cachedResults is not null && cachedResults.Count >= topK)
        {
            // Shadow had enough results — take the first topK (already ranked)
            results = cachedResults.Count == topK ? cachedResults : cachedResults.Take(topK).ToList();
        }
        else
        {
            // Full search (shadow miss, or shadow had fewer chunks than requested)
            results = await _index.SearchAsync(queryEmbedding, query, topK, ct);
        }

        if (results.Count == 0)
            return Strings.RagNoResults(query);

        // ── Format results ────────────────────────────────────────────────────
        var sb     = new StringBuilder();
        bool isSemantic = queryEmbedding is { Length: > 0 } && results[0].Score is > 0f and < 1.001f;
        var modeLabel   = isSemantic ? "semantic" : "keyword";

        sb.AppendLine($"## Codebase search: \"{query}\" ({modeLabel}, top {results.Count})");
        sb.AppendLine($"*Index: {_index.ChunkCount} chunks — {_index.Status}*");
        sb.AppendLine();

        for (int i = 0; i < results.Count; i++)
        {
            var (chunk, score) = results[i];

            var header = $"### [{i + 1}] `{chunk.RelPath}` — lines {chunk.StartLine}–{chunk.EndLine}";
            if (chunk.TypeName is not null)
                header += $" · `{chunk.TypeName}`";
            if (isSemantic && score > 0f)
                header += $" · score {score:F3}";

            sb.AppendLine(header);
            sb.AppendLine("```");

            // Truncate very long chunks to avoid overwhelming the context window
            var display = chunk.Content.Length > 900
                ? chunk.Content[..900] + "\n…(truncated)"
                : chunk.Content;
            sb.AppendLine(display);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
