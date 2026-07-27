using System.Text;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Docs;

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

    public string Description =>
        "Searches indexed external documentation (added by the user via /docs add) for passages " +
        "relevant to a natural-language query. Use this for questions about libraries, frameworks, " +
        "APIs, or product docs whose answer lives in documentation rather than in this project's code. " +
        "Returns ranked passages with their page title and source URL. " +
        "Use search_codebase instead for questions about this project's own source code.";

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
        var query = args.GetProperty("query").GetString()?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(query))
            return "query is required.";

        var topK = args.TryGetProperty("top_k", out var tk)
            ? Math.Clamp(tk.GetInt32(), 1, 10)
            : Math.Max(1, _config.RagTopK);

        if (_docs.ChunkCount == 0)
            return Strings.DocsNotReady(_docs.Status);

        // Embed the query unless the embedding circuit is open (keyword fallback then).
        float[]? queryEmbedding = null;
        if (!_client.IsEmbeddingCircuitOpen)
        {
            var model = string.IsNullOrEmpty(_config.RagEmbeddingModel)
                ? "nomic-embed-text"
                : _config.RagEmbeddingModel;
            queryEmbedding = await _client.GetEmbeddingAsync(query, model, ct);
        }

        var results = await _docs.SearchAsync(queryEmbedding, query, topK, ct);
        if (results.Count == 0)
            return Strings.DocsNoResults(query);

        var sb          = new StringBuilder();
        bool isSemantic = queryEmbedding is { Length: > 0 } && results[0].Score is > 0f and < 1.001f;
        var modeLabel   = isSemantic ? "semantic" : "keyword";

        sb.AppendLine($"## Documentation search: \"{query}\" ({modeLabel}, top {results.Count})");
        sb.AppendLine();

        for (int i = 0; i < results.Count; i++)
        {
            var (chunk, score) = results[i];

            var header = $"### [{i + 1}] {chunk.PageTitle}";
            if (isSemantic && score > 0f) header += $" · score {score:F3}";
            sb.AppendLine(header);
            sb.AppendLine($"<{chunk.Url}>");
            sb.AppendLine();

            var display = chunk.Content.Length > 900
                ? chunk.Content[..900] + "\n…(truncated)"
                : chunk.Content;
            sb.AppendLine(display);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
