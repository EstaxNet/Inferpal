using System.Linq;

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
        var lines  = CutOverLongLines(text.Replace("\r\n", "\n").Split('\n'));

        int i = 0;
        while (i < lines.Length)
        {
            int start  = i;
            int tokens = 0;
            int j      = start;

            while (j < lines.Length && tokens < TargetChunkTokens)
                tokens += Rag.ChunkText.EstimateLineTokens(lines[j++]);

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
                    ContentHash = Rag.ChunkText.Hash(content),
                });
            }

            if (end >= lines.Length - 1) break;

            // Back up by OverlapTokens so consecutive chunks share context.
            int backTokens = 0;
            int next       = end;
            while (next > start + 1 && backTokens < OverlapTokens)
                backTokens += Rag.ChunkText.EstimateLineTokens(lines[next--]);

            i = Math.Max(start + 1, next + 1); // always advance
        }

        return chunks;
    }

    /// <summary>Characters a single line may hold before it is cut — the token target, in the
    /// estimator's own unit (<c>chars / 4</c>).</summary>
    private const int MaxLineChars = TargetChunkTokens * 4;

    /// <summary>
    /// The same lines, none of them longer than one chunk's budget.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>The window below is LINE-based, and prose has no lines to rely on</b> — this class's own
    /// summary says as much and then slid a line window over it anyway. An ordinary page survives
    /// because <c>HtmlToText</c> emits a newline after every block-level close; three real shapes do
    /// not: a page that is one long <c>&lt;p&gt;</c>, a <c>&lt;pre&gt;</c> dump, and — always — a page
    /// that took <c>HtmlToText</c>'s regex-timeout fallback, which strips tags and inserts <b>no</b>
    /// newline at all. Measured: 128 231 characters on one line produced exactly ONE chunk, embedded
    /// whole (so its vector describes the opening and nothing else) and shown to the model as its
    /// first 900 characters. In the index, counted, unfindable.
    /// </para>
    /// <para>
    /// ⚠ Cut, never shrunk — the lesson the code chunker already paid for: every character of the
    /// line lands in some piece. The cut goes to the last space inside the window so a word is never
    /// split in two; a token with no space in it at all (a base64 blob, a minified line) is cut hard,
    /// because the alternative is to keep it whole, which is the defect.
    /// </para>
    /// </remarks>
    private static string[] CutOverLongLines(string[] lines)
    {
        if (lines.All(l => l.Length <= MaxLineChars)) return lines;   // the ordinary page, untouched

        var result = new List<string>(lines.Length + 8);
        foreach (var line in lines)
        {
            if (line.Length <= MaxLineChars) { result.Add(line); continue; }

            var start = 0;
            while (start < line.Length)
            {
                var take = Math.Min(MaxLineChars, line.Length - start);
                var skip = 0;
                if (start + take < line.Length)
                {
                    var lastSpace = line.LastIndexOf(' ', start + take - 1, take);
                    // The space itself is dropped, not carried: the pieces are rejoined with a
                    // newline, which is already a separator, and a trailing blank would show up at
                    // every cut in what the model reads.
                    if (lastSpace > start) { take = lastSpace - start; skip = 1; }
                }
                result.Add(line.Substring(start, take));
                start += take + skip;
            }
        }
        return [.. result];
    }

    private static string? FirstNonEmptyLine(string[] lines)
    {
        foreach (var l in lines)
        {
            var t = l.Trim();
            if (t.Length > 0) return t.Length > 120 ? t[..120] : t;
        }
        return null;
    }
}
