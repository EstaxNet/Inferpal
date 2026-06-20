using System.Text;

namespace Inferpal.Services.Rag;

/// <summary>
/// Formats the auto-retrieved RAG context block injected into a chat turn's prompt. Pure/testable:
/// the VM does the retrieval (shadow cache or a search) and passes the ranked chunks here. Chunks
/// whose file is already attached are skipped (to avoid duplicating content), and the block is
/// capped to a character budget and a maximum number of chunks.
/// </summary>
internal static class RagAutoContext
{
    public const int DefaultBudgetChars = 1500;
    public const int DefaultMaxChunks   = 3;
    private const int MaxChunkChars     = 600;

    private const string Header = "## Relevant code (auto-retrieved for this question)";

    /// <param name="results">Ranked (chunk, score) results, best first.</param>
    /// <param name="attachedPaths">Source file paths already injected as attachments — their chunks are skipped.</param>
    public static string Build(
        IReadOnlyList<(RagChunk Chunk, float Score)> results,
        ISet<string> attachedPaths,
        int budget = DefaultBudgetChars,
        int maxChunks = DefaultMaxChunks)
    {
        if (results is null || results.Count == 0) return string.Empty;

        var sb    = new StringBuilder();
        int used  = 0;
        int count = 0;

        foreach (var (chunk, _) in results)
        {
            if (count >= maxChunks) break;
            if (chunk.FilePath is { Length: > 0 } fp && attachedPaths.Contains(fp)) continue;

            var body = chunk.Content.Length > MaxChunkChars
                ? chunk.Content[..MaxChunkChars] + "\n…"
                : chunk.Content;
            var block = $"### {chunk.RelPath}:{chunk.StartLine}-{chunk.EndLine}\n```\n{body}\n```\n";

            // Stop before exceeding the budget, but always include at least one chunk.
            if (count > 0 && used + block.Length > budget) break;

            sb.Append(block).Append('\n');
            used += block.Length;
            count++;
        }

        return count == 0 ? string.Empty : Header + "\n\n" + sb.ToString().TrimEnd();
    }
}
