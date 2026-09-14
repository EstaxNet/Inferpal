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
}
