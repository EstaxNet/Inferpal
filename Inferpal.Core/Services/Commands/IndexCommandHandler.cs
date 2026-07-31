using System.Text;
using Inferpal.Config;
using Inferpal.Services.Rag;

namespace Inferpal.Services.Commands;

/// <summary>
/// Execution logic for <c>/index [rebuild]</c> — the semantic-index status report, and the manual
/// re-index trigger. Shared by both front-ends: the index service and the config live in the Core,
/// so nothing here needs an editor.
/// </summary>
internal static class IndexCommandHandler
{
    /// <summary>Fallback embedding model shown when none is configured.</summary>
    private const string DefaultEmbeddingModel = "nomic-embed-text";

    /// <param name="index">Background index service.</param>
    /// <param name="config">Current configuration (RAG toggle, model, top-K).</param>
    /// <param name="parts">Tokenised command; <c>parts[1] == "rebuild"</c> restarts indexing.</param>
    /// <param name="root">Project root; empty means "no solution open yet".</param>
    public static string Handle(ProjectIndexService index, InferpalConfig config, string[] parts, string? root)
    {
        if (parts.Length >= 2 && parts[1].Equals("rebuild", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(root))
                return "⚠ Cannot locate solution root — open a file first.";
            index.StartIndexing(root);
            return $"🔄 RAG re-indexing started: `{root}`";
        }

        var model = string.IsNullOrEmpty(config.RagEmbeddingModel) ? DefaultEmbeddingModel : config.RagEmbeddingModel;
        var sb    = new StringBuilder();

        sb.AppendLine("**RAG Index**");
        sb.AppendLine();

        if (!config.RagEnabled)
        {
            sb.AppendLine("Status: **disabled** (`ragEnabled = false` in settings)");
            sb.AppendLine();
            sb.AppendLine("Enable it to get semantic cross-file search via `search_codebase`.");
        }
        else if (index.ChunkCount == 0 && !index.IsIndexing)
        {
            sb.AppendLine($"Status: {(index.Status is { Length: > 0 } s ? s : "not started")}");
            sb.AppendLine();
            sb.AppendLine("Use `/index rebuild` to build the index manually.");
        }
        else
        {
            sb.AppendLine($"Status : {index.Status}");
            sb.AppendLine($"Chunks : {index.ChunkCount:N0}");
            sb.AppendLine($"Root   : `{index.RootDir}`");
            sb.AppendLine($"Model  : `{model}`");
            sb.AppendLine($"Top-K  : {config.RagTopK}");
            sb.AppendLine();
            sb.AppendLine("Use `/index rebuild` to force a full re-index.");
        }

        return sb.ToString().TrimEnd();
    }
}
