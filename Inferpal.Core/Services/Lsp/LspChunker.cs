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
        // ⚠ Consecutive one-line leaves form one chunk: alone, each is under the minimum and indexed nowhere (properties,
        // fields, interface signatures, enum values). Same rule as the Roslyn tier.
        var oneLiners = new List<(LspDocumentSymbol Symbol, string Name)>();
        void FlushOneLiners()
        {
            if (oneLiners.Count == 0) return;
            var name = oneLiners.Count == 1 ? oneLiners[0].Name : $"{oneLiners[0].Name}…{oneLiners[^1].Symbol.Name}";
            ChunkText.AddSymbol(name, oneLiners[0].Symbol.Range.Start.Line, oneLiners[^1].Symbol.Range.End.Line,
                        lines, filePath, relPath, chunks, minLines: 1);
            oneLiners.Clear();
        }

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
                ChunkText.AddSymbol(sym.Name, sym.Range.Start.Line, headerEnd,
                            lines, filePath, relPath, chunks);

                // Recurse into children with the container as parent context
                CollectSymbolChunks(sym.Children, lines, filePath, relPath, chunks, sym.Name);
            }
            else if (sym.Range.End.Line <= sym.Range.Start.Line)
            {
                oneLiners.Add((sym, qualifiedName));
                continue;
            }
            else
            {
                // Leaf symbol: emit the full range as one chunk
                FlushOneLiners();
                ChunkText.AddSymbol(qualifiedName,
                            sym.Range.Start.Line, sym.Range.End.Line,
                            lines, filePath, relPath, chunks);
            }
        }
        FlushOneLiners();
    }
}
