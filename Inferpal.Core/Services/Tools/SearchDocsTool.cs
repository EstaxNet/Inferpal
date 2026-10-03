using System.Text;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Docs;
using Inferpal.Services.Rag;

namespace Inferpal.Services.Tools;

/// <summary>
/// Exposes semantic search over indexed external documentation to the agentic loop.
/// Documentation sources are added by the user via <c>/docs add &lt;url&gt;</c>; this tool embeds
/// the query and retrieves the most relevant passages, falling back to keyword search when the
/// embedding model is unavailable.
/// </summary>
internal sealed class SearchDocsTool : ITool
{
    private readonly DocsIndexService  _docs;
    private readonly IInferenceProvider _client;
    private readonly InferpalConfig _config;

    public SearchDocsTool(DocsIndexService docs, IInferenceProvider client, InferpalConfig config)
    {
        _docs   = docs;
        _client = client;
        _config = config;
    }

    public string Name => "search_docs";

    /// <summary>Offered once some documentation is indexed, or being indexed: until the user runs <c>/docs add</c> —
    /// most users never do — its every answer is "no documentation indexed" (see <see cref="ITool.IsOffered"/>).</summary>
    public bool IsOffered => _docs.ChunkCount > 0 || _docs.IsIndexing;

    public string Description =>
        "Searches indexed external documentation (added by the user via /docs add) for passages " +
        "relevant to a natural-language query. Use this for questions about libraries, frameworks, " +
        "APIs, or product docs whose answer lives in documentation rather than in this project's code. " +
        "Returns ranked passages with their page title and source URL. " +
        "For this project's own source code, use search_codebase or search_in_files instead.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            query = new
            {
                type        = "string",
                description = "Natural-language description of what you are looking for in the documentation."
            },
            top_k = new
            {
                type        = "integer",
                description = "Number of passages to return (1–10). Defaults to the configured ragTopK setting."
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

        // Same reason as `search_codebase`: a number the model asked for and did not get is an
        // instruction overridden, and the report it shapes carries no trace of it.
        var (topK, topKNotice) = args.Has("top_k")
            ? ClampedArgument.Read(args, "top_k", _config.RagTopK, 1, 10)
            : (Math.Max(1, _config.RagTopK), null);

        if (_docs.ChunkCount == 0)
            return Strings.DocsNotReady(_docs.Status);

        // Embed the query unless the embedding circuit is open (keyword fallback then).
        // Bare text, like the documents of this index: the code-search formats of EmbeddingModels are not for prose.
        // The model the stored vectors came from; none = keyword search only, by choice.
        var model = _docs.QueryEmbeddingModel;

        float[]? queryEmbedding = null;
        if (model is not null && !_client.IsEmbeddingCircuitOpen)
            queryEmbedding = await _client.GetEmbeddingAsync(query, model, ct);

        var results = await _docs.SearchAsync(queryEmbedding, query, topK, ct);
        if (results.Count == 0)
            // Same reason as in SemanticSearchTool. Here semantic search is never turned off by a
            // setting: an open breaker is a FAILURE, and it is reported as one.
            return SearchDegradation.Explain(
                Strings.DocsNoResults(query),
                SearchDegradation.Classify(semanticRequested: true, queryEmbedding, modelChosen: model is not null),
                model ?? string.Empty);

        var sb = new StringBuilder();
        // ⚠ The label is read from ALL the hits, and each one's provenance from the data. Deduced
        // from `results[0].Score`, an exact-term query — the case the lexical half exists for — puts
        // a BM25-only hit first and the whole report announced itself `keyword`; and on the purely
        // lexical fallback a BM25 score under 1.001 passed for a similarity.
        var cosines   = 0;
        foreach (var h in results) if (h.IsCosine) cosines++;
        var modeLabel = RagResultPresentation.ModeLabel(
            queryEmbedding is { Length: > 0 }, results.Count, cosines);

        sb.AppendLine($"## Documentation search: \"{query}\" ({modeLabel}, top {results.Count})");
        sb.AppendLine();

        var bodies    = new StringBuilder();
        var locations = new StringBuilder();
        var firstByLocation = 0;
        for (int i = 0; i < results.Count; i++)
        {
            var hit   = results[i];
            var chunk = hit.Chunk;

            var header = $"### [{i + 1}] {chunk.PageTitle}";
            if (RagResultPresentation.ShowsScore(hit.IsCosine, hit.Score)) header += $" · score {hit.Score:F3}";

            var display = chunk.Content.Length > 900
                ? SafeTruncate.Truncate(chunk.Content, 900) + "\n…(truncated)"
                : chunk.Content;
            var entry = $"{header}\n<{chunk.Url}>\n\n{display}\n\n";

            // In rank order: once one body does not fit, the rest go by location (RankedResultBudget).
            if (firstByLocation == 0 && (i == 0 || bodies.Length + entry.Length <= RankedResultBudget.BodyChars))
                bodies.Append(entry);
            else
            {
                if (firstByLocation == 0) firstByLocation = i + 1;
                locations.AppendLine($"- {header[4..]} <{chunk.Url}>");
            }
        }

        if (firstByLocation > 0)
            sb.AppendLine(RankedResultBudget.Note(firstByLocation, results.Count,
                "a narrower query brings them back in full")).AppendLine();
        sb.Append(bodies);
        if (locations.Length > 0) sb.Append(locations).AppendLine();

        // ⚠ The same footer `search_codebase` prints, for the same reason: the model is the one that
        // will act on "it is not in the documentation", and a corpus with chunks held without a
        // vector cannot be reached by the semantic half at all. Without this the answer ended on the
        // last excerpt, and a three-quarters-blind index looked like a complete one.
        sb.AppendLine($"*Docs: {_docs.ChunkCount} chunks — {_docs.Status}*");

        return ClampedArgument.Above(topKNotice, sb.ToString().TrimEnd());
    }
}
