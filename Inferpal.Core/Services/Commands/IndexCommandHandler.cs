using System.Text;
using Inferpal.Config;
using Inferpal.Localization;
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
        // Two shapes only: a mistyped argument that returned the report would suggest a re-index that
        // never happened.
        var rebuild = parts.Length == 2 && parts[1].Equals("rebuild", StringComparison.OrdinalIgnoreCase);
        if (parts.Length >= 2 && !rebuild)
            return Strings.SlashUsage("/index [rebuild]");

        if (rebuild)
        {
            if (string.IsNullOrEmpty(root))
                return Strings.IndexNoRoot;
            index.StartIndexing(root);
            return Strings.IndexRebuildStarted(root);
        }

        var model = string.IsNullOrEmpty(config.RagEmbeddingModel) ? DefaultEmbeddingModel : config.RagEmbeddingModel;
        var sb    = new StringBuilder();

        sb.AppendLine(Strings.IndexTitle);
        sb.AppendLine();

        if (!config.RagEnabled)
        {
            sb.AppendLine(Strings.IndexDisabled);
            sb.AppendLine();
            sb.AppendLine(Strings.IndexEnableHint);
        }
        else if (index.ChunkCount == 0 && !index.IsIndexing)
        {
            sb.AppendLine(Strings.IndexStatusLine(index.Status is { Length: > 0 } s ? s : Strings.IndexNotStarted));
            sb.AppendLine();
            sb.AppendLine(Strings.IndexBuildHint);
        }
        else
        {
            // The status value itself comes from the indexing service, whose state is English by convention.
            sb.AppendLine(Strings.IndexStatusLine(index.Status));
            sb.AppendLine(Strings.IndexChunksLine(index.ChunkCount.ToString("N0")));
            sb.AppendLine(Strings.IndexRootLine(index.RootDir));
            sb.AppendLine(Strings.IndexModelLine(model));
            sb.AppendLine(Strings.IndexTopKLine(config.RagTopK));
            sb.AppendLine();
            sb.AppendLine(Strings.IndexForceHint);
        }

        return sb.ToString().TrimEnd();
    }
}
