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

    // ⚠ No "across ALL project files". The index skips what `indexExclude` excludes, folders it
    // could not list and files past the size cap — and it is PERSISTED, so a gap outlives the
    // session. That is exactly why the header below names the index state; claiming exhaustiveness
    // in the description would contradict it, before the model has even read it.
    public string Description =>
        "Semantically searches the indexed codebase for code snippets relevant to a natural language query. " +
        "Use this to find classes, methods, or patterns related to a concept without knowing the exact " +
        "file names. Returns ranked code chunks with file paths and line numbers, under a header stating " +
        "the index state: 'no result' from an index that is still building, or that skipped a folder, is " +
        "not 'not in the codebase'. Prefer this over search_in_files for conceptual or cross-file questions.";

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
        // ⚠ Not GetProperty/GetInt32: the arguments come from the model. A missing `query` throws
        // KeyNotFoundException there, and a `"top_k": "5"` — which small local models send routinely
        // — an InvalidOperationException, turning a correctable call into a tool failure.
        var query = args.Trimmed("query") ?? string.Empty;
        if (string.IsNullOrEmpty(query))
            return "query is required.";

        // ⚠ A silent clamp here shapes a CONCLUSION: asked for 25 and served 10, the header
        // still reads "top 10" and the model cannot tell a capped list from an exhausted one — it
        // concludes "this symbol appears in ten places". `ClampedArgument` says what it changed.
        // The absent case is NOT reported: `ragTopK` is the user's setting, nothing was asked for.
        var (topK, topKNotice) = args.Has("top_k")
            ? ClampedArgument.Read(args, "top_k", _config.RagTopK, 1, 10)
            : (Math.Max(1, _config.RagTopK), null);

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
        List<RagHit>? cachedResults = null;

        var model = string.IsNullOrEmpty(_config.RagEmbeddingModel)
            ? "nomic-embed-text"
            : _config.RagEmbeddingModel;

        if (_config.RagEnabled)
        {
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
        List<RagHit> results;
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

        // ⚠ Said on BOTH branches, and above the report: "nothing found" about a class the agent has
        // just written is the costliest answer this tool can give while the index is behind.
        var behind = NotReindexedNote();

        if (results.Count == 0)
            // A "nothing found" does not say the same thing depending on whether the FULL search
            // ran or only its lexical half did. A model reading a flat negative concludes the code
            // does not exist and stops looking.
            return behind + SearchDegradation.Explain(
                Strings.RagNoResults(query),
                SearchDegradation.Classify(_config.RagEnabled, queryEmbedding),
                model);

        // ── Format results ────────────────────────────────────────────────────
        var sb     = new StringBuilder();
        // The label is read from ALL the results, and each one's provenance from RagHit.IsCosine:
        // inferring it from the FIRST result's score announced "keyword" as soon as a purely
        // lexical hit came first — the very case the lexical half exists for.
        var modeLabel = RagResultPresentation.ModeLabel(queryEmbedding is { Length: > 0 }, results);

        sb.AppendLine($"## Codebase search: \"{query}\" ({modeLabel}, top {results.Count})");
        sb.AppendLine($"*Index: {_index.ChunkCount} chunks — {_index.Status}*");
        sb.AppendLine();

        for (int i = 0; i < results.Count; i++)
        {
            var hit = results[i];
            var chunk = hit.Chunk;

            var header = $"### [{i + 1}] `{chunk.RelPath}` — lines {chunk.StartLine}–{chunk.EndLine}";
            if (chunk.TypeName is not null)
                header += $" · `{chunk.TypeName}`";
            if (RagResultPresentation.ShowsScore(hit.IsCosine, hit.Score))
                header += $" · score {hit.Score:F3}";

            sb.AppendLine(header);
            sb.AppendLine("```");

            // Truncate very long chunks to avoid overwhelming the context window
            var display = chunk.Content.Length > 900
                ? SafeTruncate.Truncate(chunk.Content, 900) + "\n…(truncated)"
                : chunk.Content;
            sb.AppendLine(display);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        return ClampedArgument.Above(topKNotice, behind + sb.ToString().TrimEnd());
    }

    /// <summary>
    /// The files the index has not caught up with, named — or nothing.
    /// </summary>
    /// <remarks>
    /// A file saved while the chat is busy is re-indexed once it is idle: the agent's own writes stay
    /// out of the index for the rest of its turn. Model-facing and structural: English.
    /// </remarks>
    private string NotReindexedNote()
    {
        var behind = _index.NotYetReindexed;
        if (behind.Count == 0) return string.Empty;

        var root  = _index.RootDir;
        var names = behind.Take(5).Select(p => string.IsNullOrEmpty(root) ? Path.GetFileName(p) : Path.GetRelativePath(root, p));
        var more  = behind.Count > 5 ? $" (+{behind.Count - 5} more)" : string.Empty;
        return $"Note: {behind.Count} changed file(s) not re-indexed yet — these results show them as they were "
             + $"before the change: {string.Join(", ", names)}{more}. The index catches up once the chat is idle; "
             + "search_in_files reads them as they are now.\n\n";
    }
}
