using System.IO;
using Inferpal.Config;
using Inferpal.Services.Lsp;

namespace Inferpal.Services.Rag;

/// <summary>
/// Background service that indexes all source files in the solution for semantic RAG search.
/// </summary>
/// <remarks>
/// Lifecycle:
/// <list type="number">
///   <item>Call <see cref="StartIndexing"/> with the solution root directory.</item>
///   <item>The service enumerates source files, chunks them, and requests Ollama embeddings.</item>
///   <item>Progress and results are available via <see cref="Status"/>, <see cref="ChunkCount"/>, <see cref="IsIndexing"/>.</item>
///   <item>A <see cref="System.IO.FileSystemWatcher"/> watches the root for file changes and re-indexes
///       modified files after a 5-second debounce.</item>
/// </list>
/// Thread-safety: all internal state is protected by <see cref="_chunkLock"/>.
/// </remarks>
internal sealed class ProjectIndexService : IDisposable
{
    private readonly IInferenceProvider    _client;
    private readonly InferpalConfig     _config;
    private readonly LspSemanticProvider   _lsp;

    // Keyed by file path (case-insensitive) — O(1) file-targeted ops vs O(N) linear scan.
    private readonly Dictionary<string, List<RagChunk>> _chunksByFile =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _chunkLock = new(1, 1);
    private readonly HashSet<string>     _pendingRebuild = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource?    _cts;
    private FileSystemWatcher?          _watcher;
    private System.Threading.Timer?     _debounceTimer;

    // ── Shadow search cache ────────────────────────────────────────────────────
    // Pre-computed while the user is still typing; consumed by SemanticSearchTool
    // to skip the embedding round-trip when the agent query matches the typed prompt.
    // The three values are bundled in one immutable record and published via a single
    // volatile reference assignment, so a reader never sees a torn (half-updated) cache.
    private sealed record ShadowCache(
        string Query, float[] Embedding, List<(RagChunk Chunk, float Score)> Results);

    private volatile ShadowCache? _shadow      = null;
    private readonly SemaphoreSlim _shadowLock = new(1, 1);

    // ── Interactive priority gate ──────────────────────────────────────────────
    // Inferpal talks to a single Ollama backend on one GPU. While an interactive chat/agent
    // request is in flight, background indexing MUST yield, or the continuous embedding workload
    // keeps the GPU busy and the chat model never loads (observed: ollama ps shows only the
    // embedding model for 30 min, chat → timeout). This is now owned by the central GpuScheduler:
    // RunAgentAsync holds a chat lease for the whole turn, and the embedding loops below await
    // GpuScheduler.WaitForChatIdleAsync before each call so they pause without losing progress.

    // ── Public state ──────────────────────────────────────────────────────────

    /// <summary>Human-readable status message (updated throughout indexing).</summary>
    public string Status     { get; private set; } = string.Empty;

    /// <summary><c>true</c> while the indexing pass is in progress.</summary>
    public bool   IsIndexing { get; private set; }

    /// <summary>Number of chunks currently in memory.</summary>
    public int    ChunkCount { get; private set; }

    /// <summary>Solution root directory being indexed.</summary>
    public string RootDir    { get; private set; } = string.Empty;

    // ── Construction ──────────────────────────────────────────────────────────

    public ProjectIndexService(IInferenceProvider client, InferpalConfig config, LspSemanticProvider lsp)
    {
        _client = client;
        _config = config;
        _lsp    = lsp;
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a background indexing pass over all source files under <paramref name="rootDir"/>.
    /// If a previous pass is running it is cancelled before the new one begins.
    /// Also patches <c>.gitignore</c> to exclude <c>.inferpal/</c> if not already present.
    /// </summary>
    public void StartIndexing(string rootDir)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts    = new CancellationTokenSource();
        RootDir = rootDir;
        PatchGitIgnore(rootDir);
        _ = Task.Run(() => RunIndexingAsync(rootDir, _cts.Token));
    }

    /// <summary>
    /// Ensures <c>.gitignore</c> in <paramref name="rootDir"/> contains entries for
    /// Inferpal's data directories. Silently no-ops if git is not present or writing fails.
    /// </summary>
    private static void PatchGitIgnore(string rootDir)
    {
        try
        {
            // Only patch when inside a git repository
            var gitDir = Path.Combine(rootDir, ".git");
            if (!Directory.Exists(gitDir)) return;

            var gitIgnorePath = Path.Combine(rootDir, ".gitignore");

            // Entries we want to ensure are present
            var entries = new[]
            {
                "# Inferpal AI assistant",
                ".inferpal/",
            };

            string existing = File.Exists(gitIgnorePath)
                ? File.ReadAllText(gitIgnorePath)
                : string.Empty;

            // Check if any of our entries are already there
            if (entries.All(e => e.StartsWith('#') || existing.Contains(e)))
                return; // all data entries present, nothing to add

            // Append a blank line separator + our block
            var needsNewline = existing.Length > 0 && !existing.EndsWith('\n');
            var toAppend = (needsNewline ? Environment.NewLine : string.Empty)
                         + Environment.NewLine
                         + string.Join(Environment.NewLine, entries)
                         + Environment.NewLine;

            File.AppendAllText(gitIgnorePath, toAppend, System.Text.Encoding.UTF8);
        }
        catch { /* best-effort — never crash the indexer */ }
    }

    /// <summary>
    /// Searches the index for the most relevant chunks.
    /// </summary>
    /// <param name="queryEmbedding">Embedding of the search query; when <c>null</c> or empty, falls back to keyword search.</param>
    /// <param name="keywordFallback">Plaintext query used when semantic search is unavailable.</param>
    /// <param name="topK">Maximum number of results to return.</param>
    public async Task<List<(RagChunk Chunk, float Score)>> SearchAsync(
        float[]? queryEmbedding,
        string?  keywordFallback,
        int      topK,
        CancellationToken ct)
    {
        // Snapshot the chunk list under the lock, then run the (potentially O(N)) similarity
        // computation OUTSIDE the lock so a large search never blocks concurrent re-indexing.
        List<RagChunk> allChunks;
        await _chunkLock.WaitAsync(ct);
        try
        {
            if (_chunksByFile.Count == 0) return [];
            allChunks = _chunksByFile.Values.SelectMany(v => v).ToList();
        }
        finally
        {
            _chunkLock.Release();
        }

        // Candidate pool per side, kept wider than topK so the two rankings overlap enough for RRF.
        var pool = Math.Max(topK * 5, 50);

        // ── Vector side (cosine) ──────────────────────────────────────────
        // Indices into allChunks, best first; threshold filter preserved (Global Priority Guard).
        List<(int Idx, float Cos)> vector = [];
        if (queryEmbedding is { Length: > 0 })
        {
            vector = allChunks
                .Select((c, i) => (Idx: i, Cos: c.Embedding is { Length: > 0 } ? CosineSimilarity(queryEmbedding, c.Embedding!) : 0f))
                .Where(x => x.Cos >= _config.RagSimilarityThreshold)
                .OrderByDescending(x => x.Cos)
                .Take(pool)
                .ToList();
        }

        // ── Lexical side (BM25) ───────────────────────────────────────────
        // Catches exact identifiers / symbol & file names that weak local embeddings dilute. Tokens
        // include the chunk's type and relative path so name-based queries score strongly.
        List<(int Idx, double Score)> lexical = [];
        if (!string.IsNullOrWhiteSpace(keywordFallback))
        {
            var queryTokens = CodeTokenizer.Tokenize(keywordFallback);
            if (queryTokens.Count > 0)
            {
                var docs = allChunks
                    .Select(c => CodeTokenizer.Tokenize($"{c.Content}\n{c.TypeName}\n{c.RelPath}"))
                    .ToList<IReadOnlyList<string>>();
                lexical = new Bm25Index(docs).Rank(queryTokens, pool);
            }
        }

        // ── Fuse / fall back ──────────────────────────────────────────────
        // Display score stays the cosine similarity (meaningful to the user); ranking is the fusion.
        if (vector.Count > 0 && lexical.Count > 0)
        {
            var cosByIdx = vector.ToDictionary(x => x.Idx, x => x.Cos);
            var fused = ReciprocalRankFusion.Fuse(new[]
            {
                vector.Select(x => x.Idx).ToList(),
                lexical.Select(x => x.Idx).ToList(),
            });
            return fused.Take(topK).Select(i => (allChunks[i], cosByIdx.GetValueOrDefault(i, 0f))).ToList();
        }

        if (vector.Count > 0)
            return vector.Take(topK).Select(x => (allChunks[x.Idx], x.Cos)).ToList();

        if (lexical.Count > 0)
            return lexical.Take(topK).Select(x => (allChunks[x.Idx], (float)x.Score)).ToList();

        return [];
    }

    // ── Shadow search ──────────────────────────────────────────────────────────

    /// <summary>
    /// Pre-warms the shadow cache by embedding <paramref name="query"/> and running a full
    /// search in the background while the user is still typing.
    /// Returns immediately (non-blocking) if another pre-warm is already in progress.
    /// </summary>
    public async Task ShadowPreWarmAsync(string query, string model, CancellationToken ct)
    {
        if (ChunkCount == 0 || _client.IsEmbeddingCircuitOpen) return;

        // Non-blocking: skip if another pre-warm is already running
        if (!await _shadowLock.WaitAsync(0, ct)) return;
        try
        {
            var embedding = await _client.GetEmbeddingAsync(query, model, ct);
            if (embedding is null) return;

            var results = await SearchAsync(embedding, query, Math.Max(1, _config.RagTopK), ct);

            // Publish all three values atomically via a single reference assignment.
            _shadow = new ShadowCache(query, embedding, results);
        }
        catch (OperationCanceledException) { /* user kept typing — expected */ }
        catch { /* best-effort, never propagate */ }
        finally
        {
            _shadowLock.Release();
        }
    }

    /// <summary>
    /// Returns the pre-computed embedding and results if <paramref name="query"/> exactly
    /// matches the last shadow query (case-insensitive); otherwise returns (<c>null</c>, <c>null</c>).
    /// </summary>
    public (float[]? Embedding, List<(RagChunk Chunk, float Score)>? Results) TryGetShadow(string query)
    {
        // Single volatile read — the captured reference is immutable, so no tearing.
        var shadow = _shadow;
        if (shadow is not null && string.Equals(shadow.Query, query, StringComparison.OrdinalIgnoreCase))
            return (shadow.Embedding, shadow.Results);
        return (null, null);
    }

    // ── Indexing loop ──────────────────────────────────────────────────────────

    private async Task RunIndexingAsync(string rootDir, CancellationToken ct)
    {
        IsIndexing = true;
        Status     = "RAG: starting indexer…";

        try
        {
            var db = new RagDatabase(rootDir);

            // ── Load existing index from disk ─────────────────────────────────
            var loaded = await db.LoadAsync(ct);
            if (loaded.Count > 0)
            {
                await ApplyChunksAsync(loaded, ct);
                Status = $"RAG: {ChunkCount} chunks loaded (verifying changes…)";
            }

            // ── Enumerate source files ────────────────────────────────────────
            var files = EnumerateSourceFiles(rootDir).ToList();
            if (files.Count == 0)
            {
                Status    = "RAG: no source files found.";
                IsIndexing = false;
                return;
            }

            // Build lookup of existing chunks by file path for incremental updates
            var existingByPath = new Dictionary<string, List<RagChunk>>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in loaded)
            {
                if (!existingByPath.TryGetValue(c.FilePath, out var list))
                    existingByPath[c.FilePath] = list = [];
                list.Add(c);
            }

            var embModel   = EmbeddingModel;
            var newChunks  = new List<RagChunk>(loaded.Count + 64);

            for (int fi = 0; fi < files.Count; fi++)
            {
                ct.ThrowIfCancellationRequested();
                Status = $"RAG: {fi + 1}/{files.Count} — {Path.GetFileName(files[fi])}";

                try
                {
                    var content    = await File.ReadAllTextAsync(files[fi], ct);
                    var fileChunks = await ChunkFileAsync(files[fi], content, rootDir, ct);

                    foreach (var chunk in fileChunks)
                    {
                        ct.ThrowIfCancellationRequested();

                        // Reuse existing embedding if hash matches
                        if (existingByPath.TryGetValue(files[fi], out var oldList))
                        {
                            var existing = oldList.FirstOrDefault(c =>
                                c.StartLine == chunk.StartLine &&
                                c.ContentHash == chunk.ContentHash);
                            if (existing?.Embedding is { Length: > 0 })
                            {
                                chunk.Embedding = existing.Embedding;
                                newChunks.Add(chunk);
                                continue;
                            }
                        }

                        // Request a new embedding from Ollama.
                        // Skip silently when the embedding circuit breaker is open
                        // (3 consecutive failures → 2-min cooldown) — index continues
                        // without embeddings; keyword fallback still works.
                        if (_config.RagEnabled && !_client.IsEmbeddingCircuitOpen)
                        {
                            // Yield the shared Ollama backend to any in-flight interactive request.
                            await GpuScheduler.WaitForChatIdleAsync(ct);
                            var emb = await _client.GetEmbeddingAsync(chunk.Content, embModel, ct);
                            chunk.Embedding = emb; // null if model unavailable
                        }

                        newChunks.Add(chunk);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* skip unreadable files */ }

                // Update in-memory index every 20 files and at the end
                if (fi % 20 == 0 || fi == files.Count - 1)
                    await ApplyChunksAsync(newChunks, ct);

                // Small throttle every 5 files to avoid flooding Ollama
                if (fi % 5 == 4)
                    await Task.Delay(80, ct);
            }

            // ── Persist final index ───────────────────────────────────────────
            await ApplyChunksAsync(newChunks, ct);
            await db.SaveAsync(newChunks, ct);
            var embStatus = _client.IsEmbeddingCircuitOpen ? " (embedding ⚠ circuit open, keyword fallback)" : string.Empty;
            Status = $"RAG: ✅ {ChunkCount} chunks from {files.Count} files{embStatus}";

            // ── Watch for file changes ────────────────────────────────────────
            SetupFileWatcher(rootDir);
        }
        catch (OperationCanceledException)
        {
            Status = "RAG: indexing cancelled.";
        }
        catch (Exception ex)
        {
            Status = $"RAG: error — {ex.Message}";
        }
        finally
        {
            IsIndexing = false;
        }
    }

    // ── File watching ──────────────────────────────────────────────────────────

    private void SetupFileWatcher(string rootDir)
    {
        _watcher?.Dispose();
        try
        {
            _watcher = new FileSystemWatcher(rootDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter          = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents   = true,
            };
            foreach (var ext in CodeChunker.SupportedExtensions)
                _watcher.Filters.Add($"*{ext}");

            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
            _watcher.Renamed += (_, e) =>
            {
                OnFileChangedCore(e.OldFullPath);
                OnFileChangedCore(e.FullPath);
            };
        }
        catch { /* file watching is best-effort */ }
    }

    private void OnFileChanged(object _, FileSystemEventArgs e) => OnFileChangedCore(e.FullPath);

    private void OnFileChangedCore(string path)
    {
        lock (_pendingRebuild) _pendingRebuild.Add(path);

        // Debounce — wait 5 s after the last change before re-indexing
        _debounceTimer?.Dispose();
        _debounceTimer = new System.Threading.Timer(
            OnDebounceElapsed, null,
            dueTime: 5_000, period: System.Threading.Timeout.Infinite);
    }

    private void OnDebounceElapsed(object? _)
    {
        string[] pending;
        lock (_pendingRebuild)
        {
            pending = [.. _pendingRebuild];
            _pendingRebuild.Clear();
        }
        if (pending.Length > 0 && !string.IsNullOrEmpty(RootDir))
            _ = Task.Run(() => ReIndexFilesAsync(pending, RootDir));
    }

    private async Task ReIndexFilesAsync(string[] changedFiles, string rootDir)
    {
        var ct       = _cts?.Token ?? CancellationToken.None;
        var db       = new RagDatabase(rootDir);
        var embModel = EmbeddingModel;

        foreach (var file in changedFiles)
        {
            if (!File.Exists(file))
            {
                // File deleted — O(1) removal from dict + DB
                await _chunkLock.WaitAsync(ct);
                try
                {
                    _chunksByFile.Remove(file);
                    ChunkCount = _chunksByFile.Values.Sum(l => l.Count);
                }
                finally { _chunkLock.Release(); }

                try { await db.DeleteFileAsync(file, ct); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Diagnostics.Swallow("ProjectIndexService.DeleteFile", ex); }
                continue;
            }

            if (!CodeChunker.SupportedExtensions.Contains(
                    Path.GetExtension(file))) continue;

            try
            {
                var content    = await File.ReadAllTextAsync(file, ct);
                var fileChunks = await ChunkFileAsync(file, content, rootDir, ct);

                if (_config.RagEnabled && !_client.IsEmbeddingCircuitOpen)
                {
                    foreach (var chunk in fileChunks)
                    {
                        if (_client.IsEmbeddingCircuitOpen) break;
                        // Yield the shared Ollama backend to any in-flight interactive request.
                        await GpuScheduler.WaitForChatIdleAsync(ct);
                        var emb = await _client.GetEmbeddingAsync(chunk.Content, embModel, ct);
                        chunk.Embedding = emb;
                    }
                }

                // Update memory — O(1) dict assignment replaces old chunks for this file
                await _chunkLock.WaitAsync(ct);
                try
                {
                    _chunksByFile[file] = fileChunks;
                    ChunkCount = _chunksByFile.Values.Sum(l => l.Count);
                }
                finally { _chunkLock.Release(); }

                // Surgical SQLite write — only this file's rows are touched
                try { await db.SaveFileAsync(file, fileChunks, ct); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Diagnostics.Swallow("ProjectIndexService.SaveFile", ex); }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Diagnostics.Swallow("ProjectIndexService.ReIndexFile", ex); }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private string EmbeddingModel =>
        string.IsNullOrEmpty(_config.RagEmbeddingModel)
            ? "nomic-embed-text"
            : _config.RagEmbeddingModel;

    private async Task ApplyChunksAsync(List<RagChunk> chunks, CancellationToken ct)
    {
        await _chunkLock.WaitAsync(ct);
        try
        {
            _chunksByFile.Clear();
            foreach (var c in chunks)
            {
                if (!_chunksByFile.TryGetValue(c.FilePath, out var list))
                    _chunksByFile[c.FilePath] = list = [];
                list.Add(c);
            }
            ChunkCount = chunks.Count;
        }
        finally
        {
            _chunkLock.Release();
        }
    }

    /// <summary>Cosine similarity between two vectors (must have equal length; returns 0 if dimensions differ).</summary>
    private static float CosineSimilarity(float[] a, float[] b)
    {
        // Vectors from different embedding models have incompatible dimensions.
        // Truncating silently produces a meaningless similarity score; return 0 instead.
        if (a.Length != b.Length) return 0f;
        int   len   = a.Length;
        float dot   = 0f;
        float normA = 0f;
        float normB = 0f;

        for (int i = 0; i < len; i++)
        {
            dot   += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        return (normA > 0f && normB > 0f)
            ? dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB))
            : 0f;
    }

    /// <summary>
    /// Collects source files under <paramref name="rootDir"/>, skipping generated
    /// artifacts (bin, obj, .git, node_modules, .vs) and oversized files.
    /// </summary>
    private static List<string> EnumerateSourceFiles(string rootDir)
    {
        static bool IsExcluded(string path) =>
            path.Contains(@"\obj\",          StringComparison.Ordinal) ||
            path.Contains(@"\bin\",          StringComparison.Ordinal) ||
            path.Contains(@"\.git\",         StringComparison.Ordinal) ||
            path.Contains(@"\node_modules\", StringComparison.Ordinal) ||
            path.Contains(@"\.vs\",          StringComparison.Ordinal) ||
            path.Contains(@"\dist\",         StringComparison.Ordinal) ||
            path.Contains("/obj/",           StringComparison.Ordinal) ||
            path.Contains("/bin/",           StringComparison.Ordinal) ||
            path.Contains("/node_modules/",  StringComparison.Ordinal) ||
            path.Contains("/.git/",          StringComparison.Ordinal);

        var result = new List<string>();
        try
        {
            foreach (var ext in CodeChunker.SupportedExtensions)
            {
                foreach (var f in Directory.EnumerateFiles(
                             rootDir, $"*{ext}", SearchOption.AllDirectories))
                {
                    if (!IsExcluded(f) && new FileInfo(f).Length < CodeChunker.MaxFileSizeBytes)
                        result.Add(f);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Swallow("ProjectIndexService.CollectFiles", ex); }
        return result;
    }

    // ── Chunking ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns chunks for <paramref name="filePath"/> from the in-memory dictionary (O(1))
    /// when available, or falls back to a targeted SQLite query using <c>idx_chunks_file</c>.
    /// </summary>
    public async Task<List<RagChunk>> GetFileChunksAsync(
        string filePath, string rootDir, CancellationToken ct)
    {
        await _chunkLock.WaitAsync(ct);
        try
        {
            if (_chunksByFile.TryGetValue(filePath, out var inMemory))
                return [.. inMemory];
        }
        finally { _chunkLock.Release(); }

        // Cold path: not yet in memory — query SQLite directly
        try { return await new RagDatabase(rootDir).LoadFileAsync(filePath, ct); }
        catch { return []; }
    }

    /// <summary>
    /// Routes file chunking through the 3-tier priority chain:
    /// Tier 1 — Roslyn syntax tree (C# only, always on).
    /// Tier 2 — LSP document symbols (TS/JS/Python/Go/Rust, opt-in via <c>LspEnabled</c>).
    /// Tier 3 — Regex sliding-window fallback (<see cref="CodeChunker"/>).
    /// </summary>
    private Task<List<RagChunk>> ChunkFileAsync(
        string filePath, string content, string rootDir, CancellationToken ct)
    {
        var ext = Path.GetExtension(filePath);

        // Tier 1: Roslyn for C# — semantic boundaries + XML doc trivia, no LSP overhead
        if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(RoslynChunker.Chunk(filePath, content, rootDir));

        // Tier 2: LSP for TypeScript/JS/Python/Go/Rust when the feature is enabled
        if (_config.LspEnabled &&
            LspSemanticProvider.GetLanguageId(ext) is not null)
        {
            return LspChunker.ChunkAsync(filePath, content, rootDir, _lsp, ct);
        }

        // Tier 3: regex sliding-window fallback
        return Task.FromResult(CodeChunker.Chunk(filePath, content, rootDir));
    }

    // ── IDisposable ────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
        _chunkLock.Dispose();
        _shadowLock.Dispose();
    }
}
