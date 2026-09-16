using System.IO;
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
///   <item>On any failure the call falls back to <see cref="CodeChunker.Chunk"/> — and the
///     fallback is <b>said</b>, once: a server that is missing, dead or timed out is traced by
///     <see cref="LspSemanticProvider"/> (once per session and per language, with the name of the
///     executable to install), and the unexpected is traced here. The one mute fallback left is
///     deliberate: "no symbols" on a file that legitimately has none.</item>
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
        catch (Exception ex)
        {
            // ⚠ This catch is DEFENSIVE, and that has to be said precisely: the main case — a
            // server missing, dead or timed out — is already traced by
            // `LspSemanticProvider.MarkFailed`, once per session and per language, with the name of
            // the executable to install; and a malformed symbol range does not throw, `TryAddChunk`
            // clamps it and skips it. What is left is therefore the unexpected: something escaping
            // a `GetSymbolsAsync` documented as returning `null` on error (a broken pipe, a JSON-RPC
            // framing error). Rare, but it was MUTE, and it was the last mute fallback of the
            // chunker's three tiers: the index loses its symbol boundaries for a whole language.
            //
            // Once per language AND per cause — this chunker is called per file.
            var lang = LspSemanticProvider.GetLanguageId(Path.GetExtension(filePath)) ?? "?";
            Diagnostics.RecordOnce(
                "Lsp",
                $"Unexpected failure while reading {lang} symbols "
                + $"({ex.GetType().Name}: {ex.Message}); indexing falls back to the heuristic chunker.",
                lang + "/" + ex.GetType().Name);
        }

        return CodeChunker.Chunk(filePath, content, rootDir);
    }

    // ── Symbol → RagChunk conversion ──────────────────────────────────────────

    /// <remarks><c>internal</c> rather than <c>private</c> for the same reason as
    /// <c>GpuScheduler.RefreshBusyMarker</c>: <see cref="LspSemanticProvider"/> is <c>sealed</c>
    /// and not virtual, so a test cannot reach this splitting through <see cref="ChunkAsync"/> —
    /// and this is exactly the splitting that lost the tail of long symbols.</remarks>
    internal static List<RagChunk> ChunkFromSymbols(
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

        // ⚠ Past the budget, the symbol is SPLIT into consecutive pieces. It used to be SHRUNK
        // until it fitted, and its tail was indexed nowhere: semantic search could never reach the
        // end of a long TypeScript / Python / Go / Rust function, and nothing said so. ⚠ This is
        // word for word the defect the Roslyn tier fixed, with the lesson written in its comment —
        // and the LSP tier had kept it: a fix that closes the instance one saw leaves alive the
        // class one did not look for. The consequence stings: turning `lspEnabled` on made the
        // index WORSE than the regex tier, whose sliding window covers the whole file.
        var pieceStart = startLine0;
        while (pieceStart <= endLine0)
        {
            var pieceEnd = pieceStart;
            var tokens   = ChunkText.EstimateTokens(lines, pieceStart, pieceStart);
            while (pieceEnd < endLine0 &&
                   tokens + ChunkText.EstimateTokens(lines, pieceEnd + 1, pieceEnd + 1) <= MaxChunkTokens)
                tokens += ChunkText.EstimateTokens(lines, ++pieceEnd, pieceEnd);

            AddPiece(symbolName, pieceStart, pieceEnd, lines, filePath, relPath, chunks);
            pieceStart = pieceEnd + 1;
        }
    }

    private static void AddPiece(
        string   symbolName,
        int      startLine0,
        int      endLine0,
        string[] lines,
        string   filePath,
        string   relPath,
        List<RagChunk> chunks)
    {
        var text = string.Join('\n', lines, startLine0, endLine0 - startLine0 + 1).Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        chunks.Add(new RagChunk
        {
            FilePath    = filePath,
            RelPath     = relPath,
            StartLine   = startLine0 + 1,   // convert to 1-based
            EndLine     = endLine0   + 1,
            Content     = text,
            ContentHash = ChunkText.Hash(text),
            TypeName    = symbolName,
        });
    }
}
