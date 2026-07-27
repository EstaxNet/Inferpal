using System.IO;
using System.Security.Cryptography;
using System.Text;
using Inferpal.Services.Rag;

namespace Inferpal.Services.Lsp;

/// <summary>
/// Wraps <see cref="LspSemanticProvider"/> to produce <see cref="RagChunk"/> lists
/// suitable for the RAG indexing pipeline.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>For files whose language is supported by an installed language server, symbol
///     ranges from <c>textDocument/documentSymbol</c> replace the sliding-window heuristic
///     used by <see cref="CodeChunker"/> for non-C# files.</item>
///   <item>Container symbols (class, module, …) with children are represented as one chunk
///     per child (function / method / property), prefixed with the parent name for
///     embedding context.</item>
///   <item>On any failure (server not found, timeout, parse error) the call falls back
///     silently to <see cref="CodeChunker.Chunk"/>.</item>
/// </list>
/// </remarks>
internal static class LspChunker
{
    /// <summary>Hard cap in estimated tokens (chars / 4). LSP symbols are already semantic so we allow 2× the RAG target.</summary>
    private const int MaxChunkTokens = 1000;

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Chunks <paramref name="filePath"/> using LSP symbols when possible, falling back
    /// to the regex-based <see cref="CodeChunker.Chunk"/> otherwise.
    /// </summary>
    public static async Task<List<RagChunk>> ChunkAsync(
        string filePath,
        string content,
        string rootDir,
        LspSemanticProvider lsp,
        CancellationToken ct)
    {
        try
        {
            var symbols = await lsp.GetSymbolsAsync(filePath, content, rootDir, ct);
            if (symbols is { Length: > 0 })
            {
                var chunks = ChunkFromSymbols(symbols, filePath, content, rootDir);
                if (chunks.Count > 0) return chunks;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* fall through to regex chunker */ }

        return CodeChunker.Chunk(filePath, content, rootDir);
    }

    // ── Symbol → RagChunk conversion ──────────────────────────────────────────

    private static List<RagChunk> ChunkFromSymbols(
        LspDocumentSymbol[] symbols,
        string filePath,
        string content,
        string rootDir)
    {
        var lines   = content.Split('\n');
        var relPath = Path.GetRelativePath(rootDir, filePath);
        var chunks  = new List<RagChunk>();

        CollectSymbolChunks(symbols, lines, filePath, relPath, chunks, parentName: null);
        return chunks;
    }

    /// <summary>
    /// Recursively collects chunks from the symbol tree.
    /// Container symbols (class, namespace, …) recurse into children.
    /// Leaf symbols (function, method, property, …) become a single chunk.
    /// </summary>
    private static void CollectSymbolChunks(
        LspDocumentSymbol[] symbols,
        string[] lines,
        string filePath,
        string relPath,
        List<RagChunk> chunks,
        string? parentName)
    {
        foreach (var sym in symbols)
        {
            var isContainer = sym.Kind is
                LspSymbolKind.Class     or LspSymbolKind.Interface or
                LspSymbolKind.Struct    or LspSymbolKind.Module    or
                LspSymbolKind.Namespace or LspSymbolKind.Enum      or
                LspSymbolKind.Package;

            var qualifiedName = parentName is null ? sym.Name : $"{parentName}.{sym.Name}";

            if (isContainer && sym.Children is { Length: > 0 })
            {
                // Add a small header chunk for the container declaration itself
                // (lines up to first child, capped at 10 lines)
                var firstChildLine = sym.Children.Min(c => c.Range.Start.Line);
                var headerEnd      = Math.Min(sym.Range.Start.Line + 10, firstChildLine);
                TryAddChunk(sym.Name, sym.Range.Start.Line, headerEnd,
                            lines, filePath, relPath, chunks);

                // Recurse into children with the container as parent context
                CollectSymbolChunks(sym.Children, lines, filePath, relPath, chunks, sym.Name);
            }
            else
            {
                // Leaf symbol: emit the full range as one chunk
                TryAddChunk(qualifiedName,
                            sym.Range.Start.Line, sym.Range.End.Line,
                            lines, filePath, relPath, chunks);
            }
        }
    }

    /// <summary>
    /// Creates a <see cref="RagChunk"/> for the given 0-based line range and appends it
    /// to <paramref name="chunks"/>. Skips trivial (≤ 1 line) or whitespace-only chunks.
    /// </summary>
    private static void TryAddChunk(
        string   symbolName,
        int      startLine0,   // 0-based (as returned by LSP)
        int      endLine0,
        string[] lines,
        string   filePath,
        string   relPath,
        List<RagChunk> chunks)
    {
        if (endLine0 < startLine0 || startLine0 >= lines.Length) return;
        endLine0 = Math.Min(endLine0, lines.Length - 1);

        int lineCount = endLine0 - startLine0 + 1;
        if (lineCount < 2) return; // skip trivial single-line entries

        // Hard cap: shrink by 25% steps until under token budget
        while (lineCount > 2 && EstimateTokens(lines, startLine0, endLine0) > MaxChunkTokens)
        {
            int cut = Math.Max(1, lineCount / 4);
            endLine0  -= cut;
            lineCount  = endLine0 - startLine0 + 1;
        }

        var text = string.Join('\n', lines, startLine0, lineCount).Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        chunks.Add(new RagChunk
        {
            FilePath    = filePath,
            RelPath     = relPath,
            StartLine   = startLine0 + 1,   // convert to 1-based
            EndLine     = endLine0   + 1,
            Content     = text,
            ContentHash = Md5Hex(text),
            TypeName    = symbolName,
        });
    }

    private static int EstimateTokens(string[] lines, int start, int end)
    {
        int total = 0;
        for (int i = start; i <= end && i < lines.Length; i++)
            total += Math.Max(1, (lines[i].Length + 1) / 4);
        return total;
    }

    private static string Md5Hex(string text)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
