using System.IO;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Rag;

/// <summary>
/// Splits a source file into semantically coherent chunks suitable for RAG embedding.
/// </summary>
/// <remarks>
/// Strategy per language:
/// <list type="bullet">
///   <item>C# — extract each top-level type (class/interface/struct/record/enum) as a separate
///     chunk.  Types exceeding <see cref="TargetChunkTokens"/> are split further at method boundaries.</item>
///   <item>All other supported languages — token-aware sliding window targeting
///     <see cref="TargetChunkTokens"/> with <see cref="OverlapTokens"/> of overlap.</item>
/// </list>
/// Files smaller than <see cref="MinChunkLines"/> lines are emitted as a single chunk.
/// </remarks>
internal static class CodeChunker
{
    // ── Configuration ─────────────────────────────────────────────────────────

    /// <summary>Target chunk size in estimated tokens (chars / 4).</summary>
    private const int TargetChunkTokens = 500;

    /// <summary>Overlap in tokens between consecutive sliding-window chunks.</summary>
    private const int OverlapTokens = 100;

    /// <summary>Hard cap for a single method/symbol — 2× target to accommodate large methods.</summary>
    private const int MaxChunkTokens = TargetChunkTokens * 2;

    /// <summary>Minimum chunk size — smaller fragments are skipped.</summary>
    private const int MinChunkLines = 4;

    /// <summary>Maximum file size to index (200 KB) — avoids memory issues with generated files.</summary>
    internal const long MaxFileSizeBytes = 200_000;

    /// <summary>The same cap in the unit a human reads, converted <b>once</b>: the figure reaches
    /// the user through <c>/index</c>, and a second conversion is a second answer.</summary>
    internal static int MaxFileSizeKilobytes => (int)(MaxFileSizeBytes / 1024);

    // ── Supported extensions ───────────────────────────────────────────────────

    /// <summary>Source file extensions that will be indexed.</summary>
    public static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".ts", ".tsx", ".js", ".jsx",
            ".py", ".go", ".java", ".cpp", ".c", ".h", ".hpp",
            ".rs", ".fs", ".razor", ".vue",
        };

    // ── Regex patterns for C# ─────────────────────────────────────────────────

    // Top-level type declaration (class / interface / struct / record / enum)
    private static readonly Regex _typeDecl = new(
        @"^\s*(?:(?:public|internal|private|protected|file)\s+)*" +
        @"(?:(?:abstract|sealed|static|partial|readonly|new)\s+)*" +
        @"(?:class|interface|struct|record|enum)\s+(\w+)",
        RegexOptions.Compiled, RegexBudget.Default);

    // Method or property declaration (used for intra-type splitting)
    private static readonly Regex _methodDecl = new(
        @"^\s*(?:(?:public|private|protected|internal|static|virtual|override|abstract|async|new|sealed|extern)\s+)+" +
        @"(?:[\w<>\[\]?,\s]+\s+)?(\w+)\s*[<(]",
        RegexOptions.Compiled, RegexBudget.Default);

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Splits <paramref name="content"/> into chunks and returns them.
    /// Chunks that are too small or empty are silently dropped.
    /// </summary>
    /// <param name="filePath">Absolute path of the file (used for path metadata).</param>
    /// <param name="content">Full text of the file.</param>
    /// <param name="rootDir">Solution root directory (used to compute <see cref="RagChunk.RelPath"/>).</param>
    public static List<RagChunk> Chunk(string filePath, string content, string rootDir)
    {
        // Normalise CRLF first: splitting on '\n' alone leaves a trailing '\r' on every line of a
        // Windows file, which ends up inside the embedded text and inflates the token estimate.
        var lines   = content.Replace("\r\n", "\n").Split('\n');
        var ext     = Path.GetExtension(filePath).ToLowerInvariant();
        var relPath = Path.GetRelativePath(rootDir, filePath);

        // Whole-file chunk for tiny files
        if (lines.Length <= MinChunkLines * 2)
            return MakeChunks(lines, [(0, lines.Length - 1, null)], filePath, relPath);

        var blocks = ext == ".cs"
            ? FindCSharpBlocks(lines)
            : SlidingWindowBlocks(lines);

        return MakeChunks(lines, blocks, filePath, relPath);
    }

    // ── C# block extraction ────────────────────────────────────────────────────

    private static List<(int start, int end, string? typeName)> FindCSharpBlocks(string[] lines)
    {
        var typeStarts = new List<(int line, string name)>();

        for (int i = 0; i < lines.Length; i++)
        {
            var m = _typeDecl.Match(lines[i]);
            // Skip lines inside comments (very rough heuristic: starts with //)
            if (m.Success && !lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
                typeStarts.Add((i, m.Groups[1].Value));
        }

        // Fallback to sliding window if no type declarations found
        if (typeStarts.Count == 0)
            return SlidingWindowBlocks(lines);

        var result = new List<(int, int, string?)>();

        for (int i = 0; i < typeStarts.Count; i++)
        {
            int start    = typeStarts[i].line;
            int end      = i + 1 < typeStarts.Count ? typeStarts[i + 1].line - 1 : lines.Length - 1;
            var typeName = typeStarts[i].name;

            if (ChunkText.EstimateTokens(lines, start, end) > TargetChunkTokens)
            {
                // Type is too large — split by methods
                result.AddRange(SplitByMethods(lines, start, end, typeName));
            }
            else
            {
                result.Add((start, end, typeName));
            }
        }

        return result;
    }

    private static List<(int, int, string?)> SplitByMethods(
        string[] lines, int start, int end, string? typeName)
    {
        var methodStarts = new List<int> { start };

        for (int i = start + 1; i <= end; i++)
        {
            if (_methodDecl.IsMatch(lines[i]) &&
                !lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
                methodStarts.Add(i);
        }

        var result = new List<(int, int, string?)>();

        for (int i = 0; i < methodStarts.Count; i++)
        {
            int mStart = methodStarts[i];
            int mEnd   = i + 1 < methodStarts.Count ? methodStarts[i + 1] - 1 : end;

            // ⚠ Past the budget, the member is SPLIT into consecutive pieces — never TRUNCATED
            // (`mEnd` pulled back to what fits), which indexes its tail nowhere. The same rule
            // holds in the other two chunker tiers. ⚠ Honest scope: this block only serves C# that
            // reached the regex tier, so only when Roslyn failed — the sliding window of the other
            // languages does cover the whole file.
            //
            // ⚠ A remainder shorter than MinChunkLines is absorbed by the current piece rather than
            // left aside: `MakeChunks` drops pieces that are too short, so publishing it separately
            // loses it — the same defect, smaller.
            var pieceStart = mStart;
            while (pieceStart <= mEnd)
            {
                var pieceEnd = pieceStart;
                var tok      = ChunkText.EstimateLineTokens(lines[pieceStart]);
                while (pieceEnd < mEnd &&
                       tok + ChunkText.EstimateLineTokens(lines[pieceEnd + 1]) <= MaxChunkTokens)
                    tok += ChunkText.EstimateLineTokens(lines[++pieceEnd]);

                if (mEnd - pieceEnd is > 0 and < MinChunkLines) pieceEnd = mEnd;

                result.Add((pieceStart, pieceEnd, typeName));
                pieceStart = pieceEnd + 1;
            }
        }

        return result;
    }

    // ── Sliding-window blocks ─────────────────────────────────────────────────

    private static List<(int, int, string?)> SlidingWindowBlocks(string[] lines)
    {
        var result = new List<(int, int, string?)>();
        int i = 0;

        while (i < lines.Length)
        {
            int start  = i;
            int tokens = 0;
            int j      = start;

            // Accumulate lines until we hit the token target
            while (j < lines.Length && tokens < TargetChunkTokens)
                tokens += ChunkText.EstimateLineTokens(lines[j++]);

            int end = Math.Min(j - 1, lines.Length - 1);
            result.Add((start, end, null));

            if (end >= lines.Length - 1) break;

            // Back up by OverlapTokens so consecutive chunks share context
            int backTokens = 0;
            int next       = end;
            while (next > start + 1 && backTokens < OverlapTokens)
                backTokens += ChunkText.EstimateLineTokens(lines[next--]);

            i = Math.Max(start + 1, next + 1); // always advance
        }

        return result;
    }

    // ── Chunk assembly ────────────────────────────────────────────────────────

    private static List<RagChunk> MakeChunks(
        string[] lines,
        IEnumerable<(int start, int end, string? typeName)> blocks,
        string filePath,
        string relPath)
    {
        var chunks = new List<RagChunk>();

        foreach (var (start, end, typeName) in blocks)
        {
            int lineCount = end - start + 1;
            if (lineCount < MinChunkLines) continue;

            var text = string.Join('\n', lines, start, lineCount).Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            chunks.Add(new RagChunk
            {
                FilePath    = filePath,
                RelPath     = relPath,
                StartLine   = start + 1, // 1-based
                EndLine     = end   + 1,
                Content     = text,
                ContentHash = ChunkText.Hash(text),
                TypeName    = typeName,
            });
        }

        return chunks;
    }
}
