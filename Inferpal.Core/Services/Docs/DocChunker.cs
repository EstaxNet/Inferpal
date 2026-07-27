using System.Security.Cryptography;

namespace Inferpal.Services.Docs;

/// <summary>
/// Splits the readable text of a documentation page into overlapping, token-bounded chunks
/// suitable for embedding. Prose has no syntactic boundaries, so a plain line-based sliding
/// window (target <see cref="TargetChunkTokens"/>, <see cref="OverlapTokens"/> overlap) is used —
/// the same heuristic as <see cref="Rag.CodeChunker"/> for non-C# files.
/// </summary>
internal static class DocChunker
{
    /// <summary>Target chunk size in estimated tokens (chars / 4).</summary>
    private const int TargetChunkTokens = 500;

    /// <summary>Overlap in tokens between consecutive chunks so context is not lost at boundaries.</summary>
    private const int OverlapTokens = 100;

    /// <summary>Chunks shorter than this many characters are dropped as noise.</summary>
    private const int MinChunkChars = 40;

    /// <summary>
    /// Chunks <paramref name="text"/> for a single page into <see cref="DocChunk"/> instances.
    /// </summary>
    public static List<DocChunk> Chunk(string docId, string url, string pageTitle, string text)
    {
        var chunks = new List<DocChunk>();
        var lines  = text.Replace("\r\n", "\n").Split('\n');

        int i = 0;
        while (i < lines.Length)
        {
            int start  = i;
            int tokens = 0;
            int j      = start;

            while (j < lines.Length && tokens < TargetChunkTokens)
                tokens += EstimateLineTokens(lines[j++]);

            int end = Math.Min(j - 1, lines.Length - 1);

            var slice   = lines[start..(end + 1)];
            var content = string.Join('\n', slice).Trim();

            if (content.Length >= MinChunkChars)
            {
                chunks.Add(new DocChunk
                {
                    DocId       = docId,
                    Url         = url,
                    PageTitle   = pageTitle,
                    Heading     = FirstNonEmptyLine(slice),
                    Content     = content,
                    ContentHash = ComputeMd5(content),
                });
            }

            if (end >= lines.Length - 1) break;

            // Back up by OverlapTokens so consecutive chunks share context.
            int backTokens = 0;
            int next       = end;
            while (next > start + 1 && backTokens < OverlapTokens)
                backTokens += EstimateLineTokens(lines[next--]);

            i = Math.Max(start + 1, next + 1); // always advance
        }

        return chunks;
    }

    private static int EstimateLineTokens(string line) => Math.Max(1, (line.Length + 1) / 4);

    private static string? FirstNonEmptyLine(string[] lines)
    {
        foreach (var l in lines)
        {
            var t = l.Trim();
            if (t.Length > 0) return t.Length > 120 ? t[..120] : t;
        }
        return null;
    }

    private static string ComputeMd5(string text)
    {
        var bytes = MD5.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
