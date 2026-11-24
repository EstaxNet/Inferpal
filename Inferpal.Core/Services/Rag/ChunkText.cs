using System.Security.Cryptography;
using System.Text;

namespace Inferpal.Services.Rag;

/// <summary>
/// What every chunker computes the same way — the code index's three chunkers and the documentation
/// chunker.
/// </summary>
/// <remarks>
/// The hash is persisted with each chunk and decides whether its stored embedding is reused: a hash in
/// another format misses every stored vector and re-embeds each indexed project on the next start.
/// </remarks>
internal static class ChunkText
{
    /// <summary>Lowercase hex MD5 of the UTF-8 text.</summary>
    public static string Hash(string text) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>Token estimate of one line: characters / 4 (the usual BPE approximation), at least 1.</summary>
    public static int EstimateLineTokens(string line) => Math.Max(1, (line.Length + 1) / 4);

    /// <summary>Token estimate of lines <paramref name="start"/> to <paramref name="end"/>, inclusive.</summary>
    public static int EstimateTokens(string[] lines, int start, int end)
    {
        int total = 0;
        for (int i = start; i <= end && i < lines.Length; i++)
            total += EstimateLineTokens(lines[i]);
        return total;
    }

    /// <summary>
    /// The chunks of one symbol of the Roslyn and LSP tiers, 0-based lines <paramref name="start0"/> to
    /// <paramref name="end0"/>, appended to <paramref name="chunks"/>; a symbol shorter than <paramref name="minLines"/>
    /// lines, or of whitespace alone, is none.
    /// </summary>
    /// <remarks>⚠ Past <see cref="CodeChunker.MaxChunkTokens"/>, the symbol is SPLIT into consecutive pieces — never
    /// SHRUNK until it fits, which indexes its tail nowhere: semantic search then cannot reach the end of a long method,
    /// and nothing says so. The regex tier's sliding window covers the whole file: a symbol tier must not do worse.</remarks>
    public static void AddSymbol(string symbolName, int start0, int end0, string[] lines, string filePath, string relPath,
                                 List<RagChunk> chunks, int minLines = 2)
    {
        if (end0 < start0 || start0 >= lines.Length) return;
        end0 = Math.Min(end0, lines.Length - 1);
        if (end0 - start0 + 1 < minLines) return;

        var pieceStart = start0;
        while (pieceStart <= end0)
        {
            var pieceEnd = pieceStart;
            var tokens   = EstimateTokens(lines, pieceStart, pieceStart);
            while (pieceEnd < end0 && tokens + EstimateTokens(lines, pieceEnd + 1, pieceEnd + 1) <= CodeChunker.MaxChunkTokens)
                tokens += EstimateTokens(lines, ++pieceEnd, pieceEnd);

            var text = string.Join('\n', lines, pieceStart, pieceEnd - pieceStart + 1).Trim();
            if (!string.IsNullOrWhiteSpace(text))
                chunks.Add(new RagChunk
                {
                    FilePath    = filePath,
                    RelPath     = relPath,
                    StartLine   = pieceStart + 1,   // 1-based
                    EndLine     = pieceEnd + 1,
                    Content     = text,
                    ContentHash = Hash(text),
                    TypeName    = symbolName,
                });
            pieceStart = pieceEnd + 1;
        }
    }
}
