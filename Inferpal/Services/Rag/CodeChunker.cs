using System.IO;
using System.Security.Cryptography;
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
        RegexOptions.Compiled);

    // Method or property declaration (used for intra-type splitting)
    private static readonly Regex _methodDecl = new(
        @"^\s*(?:(?:public|private|protected|internal|static|virtual|override|abstract|async|new|sealed|extern)\s+)+" +
        @"(?:[\w<>\[\]?,\s]+\s+)?(\w+)\s*[<(]",
        RegexOptions.Compiled);

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
        var lines   = content.Split('\n');
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

            if (EstimateTokens(lines, start, end) > TargetChunkTokens)
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

            // Hard cap: if method still exceeds budget, truncate line-by-line
            if (EstimateTokens(lines, mStart, mEnd) > MaxChunkTokens)
            {
                int tok = 0;
                int cut = mStart;
                while (cut <= mEnd && tok < MaxChunkTokens)
                    tok += EstimateLineTokens(lines[cut++]);
                mEnd = Math.Max(mStart + MinChunkLines, cut - 2);
            }

            result.Add((mStart, mEnd, typeName));
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
                tokens += EstimateLineTokens(lines[j++]);

            int end = Math.Min(j - 1, lines.Length - 1);
            result.Add((start, end, null));

            if (end >= lines.Length - 1) break;

            // Back up by OverlapTokens so consecutive chunks share context
            int backTokens = 0;
            int next       = end;
            while (next > start + 1 && backTokens < OverlapTokens)
                backTokens += EstimateLineTokens(lines[next--]);

            i = Math.Max(start + 1, next + 1); // always advance
        }

        return result;
    }

    // ── Token estimation (chars / 4 — standard BPE approximation) ────────────

    private static int EstimateLineTokens(string line) =>
        Math.Max(1, (line.Length + 1) / 4);

    private static int EstimateTokens(string[] lines, int start, int end)
    {
        int total = 0;
        for (int i = start; i <= end && i < lines.Length; i++)
            total += EstimateLineTokens(lines[i]);
        return total;
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
                ContentHash = ComputeMd5(text),
                TypeName    = typeName,
            });
        }

        return chunks;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string ComputeMd5(string text)
    {
        var bytes = MD5.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
