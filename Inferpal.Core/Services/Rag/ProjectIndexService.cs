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
///   <item>A <see cref="System.IO.FileSystemWatcher"/> — armed <b>before</b> the pass reads anything,
///       so nothing changed mid-pass is lost — re-indexes modified files after a 5-second debounce
///       (deferred while a pass runs; the pass drains the accumulated backlog after its final save).</item>
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

    // The watcher raises changes on several thread-pool threads at once, and Dispose can land in
    // the middle: without this lock the debounce timer is a read-modify-write race (a leaked timer
    // fires an extra re-index, or one is recreated after shutdown and touches a disposed CTS).
    private readonly object _timerLock = new();
    private volatile bool   _disposed;

    // ── Shadow search cache ────────────────────────────────────────────────────
    // Pre-computed while the user is still typing; consumed by SemanticSearchTool
    // to skip the embedding round-trip when the agent query matches the typed prompt.
    // The three values are bundled in one immutable record and published via a single
    // volatile reference assignment, so a reader never sees a torn (half-updated) cache.
    private sealed record ShadowCache(
        string Query, float[] Embedding, List<RagHit> Results);

    private volatile ShadowCache? _shadow      = null;
    private readonly SemaphoreSlim _shadowLock = new(1, 1);

    // Bumped under _chunkLock whenever the whole index is replaced. The shadow holds chunks of the
    // index it was computed against: after a workspace switch they are another project's code, so a
    // replacement clears it, and a pre-warm started before the replacement does not publish after it.
    private int _indexGeneration;

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

    /// <summary>
    /// A folder of the workspace the indexing walk could not <b>list</b>, relative to
    /// <see cref="RootDir"/> — <c>null</c> when the whole tree could be listed.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>This is the most consequential place in the product for that silence, because the index
    /// is PERSISTED.</b> A one-off tool report is wrong for one answer; an index built without a
    /// folder makes <c>search_codebase</c> and the per-turn auto-context blind to it for as long as
    /// the database lives, across restarts — while <c>/index</c>, the very screen one opens to check
    /// the index, reported a chunk count that reads as complete. The walk skips such a folder in
    /// silence (<c>EnumerationOptions.IgnoreInaccessible</c>), so it is absent from every count
    /// rather than subtracted from one: no arithmetic here could have revealed it.
    /// </remarks>
    public WorkspaceScan.WalkGap? SkippedFolder { get; private set; }

    /// <summary>Solution root directory being indexed.</summary>
    public string RootDir    { get; private set; } = string.Empty;

    /// <summary>The root the last indexing pass was started on (empty = never). <see cref="RootDir"/> can be
    /// pinned without indexing, so it does not say whether a pass ever ran.</summary>
    public string IndexedRoot { get; private set; } = string.Empty;

    // Extra exclusion patterns contributed by .inferpal/project.json. Read when the
    // root is pinned rather than per file: this sits in the enumeration loop. Additive only — the
    // profile can lengthen the built-in list, never shorten it (see IndexExclusions).
    private volatile IReadOnlyList<string> _profileExcludes = [];

    // ── Construction ──────────────────────────────────────────────────────────

    public ProjectIndexService(IInferenceProvider client, InferpalConfig config, LspSemanticProvider lsp)
    {
        _client = client;
        _config = config;
        _lsp    = lsp;
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the workspace root without starting an indexing pass. The file tools take their
    /// confinement root from <see cref="RootDir"/>, so a host embedding the Core (RAG on or off)
    /// must pin it as soon as the workspace is known; <see cref="StartIndexing"/> overwrites it.
    /// </summary>
    public void SetRoot(string rootDir)
    {
        RootDir          = rootDir;
        _profileExcludes = ProjectProfile.Load(rootDir).IndexExcludes;
        // ⚠ The watcher is armed HERE too, not only by the indexing pass, because it has a second
        // consumer that has nothing to do with RAG: the C# semantic index is cached per workspace
        // and this watcher is the only thing that invalidates it. Armed from the pass alone, it did
        // not exist at all when `ragEnabled` is off — so `analyze_impact` answered its
        // compiler-resolved section, the one it tells the model to trust over the name-matching
        // ones, from a compilation frozen at the first C# analysis of the session. `SetRoot` is
        // what both front-ends call with RAG on or off, which is exactly the property needed.
        SetupFileWatcher(rootDir);
    }

    /// <summary>
    /// Starts a background indexing pass over all source files under <paramref name="rootDir"/>.
    /// If a previous pass is running it is cancelled before the new one begins.
    /// Also patches <c>.gitignore</c> to exclude <c>.inferpal/</c> if not already present.
    /// </summary>
    public void StartIndexing(string rootDir)
    {
        var previousCts  = _cts;
        var previousPass = _indexingPass;
        try { previousCts?.Cancel(); } catch (ObjectDisposedException) { }

        var cts   = new CancellationTokenSource();
        var token = cts.Token;   // captured now: the task must not read a field a later call replaces
        _cts    = cts;
        RootDir = rootDir;
        IndexedRoot = rootDir;
        _profileExcludes = ProjectProfile.Load(rootDir).IndexExcludes;
        PatchGitIgnore(rootDir);
        // Language servers started for another workspace would otherwise run until the editor closes.
        _lsp.ReleaseSessionsExcept(rootDir);

        _indexingPass = Task.Run(async () =>
        {
            // ⚠ The cancelled pass must END first: its finally resets IsIndexing (the watcher then stops
            // deferring to the new pass) and its final replaceAll would publish content read earlier.
            if (previousPass is not null)
            {
                try { await previousPass.ConfigureAwait(false); }
                catch (Exception ex) { Diagnostics.Swallow("ProjectIndexService.PreviousPass", ex); }
            }
            previousCts?.Dispose();
            await RunIndexingAsync(rootDir, token).ConfigureAwait(false);
        });
    }

    /// <summary>The indexing pass in flight (or last run); a new pass waits for it to end.</summary>
    private Task? _indexingPass;

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

            // Only the snapshots: the rest of .inferpal/ is meant to be committed (see GitIgnorePatch).
            // The file's BOM, if any, is kept: this is the user's file, not ours.
            var bytes    = File.Exists(gitIgnorePath) ? File.ReadAllBytes(gitIgnorePath) : Array.Empty<byte>();
            var hasBom   = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var skip     = hasBom ? 3 : 0;
            var existing = new System.Text.UTF8Encoding(false).GetString(bytes, skip, bytes.Length - skip);

            var patched = GitIgnorePatch.Apply(existing);
            if (patched is null) return;

            File.WriteAllText(gitIgnorePath, patched, new System.Text.UTF8Encoding(hasBom));
        }
        catch (Exception ex) { Diagnostics.Swallow("ProjectIndexService.PatchGitIgnore", ex); }
    }

    /// <summary>The flattened corpus and its BM25 index, for one <see cref="_contentVersion"/>.</summary>
    private sealed record SearchSnapshot(int Version, List<RagChunk> Chunks, Bm25Index Lexical);

    /// <summary>
    /// Reused while the index does not change: rebuilding the BM25 index over the whole corpus on
    /// every query — the pre-search while typing, the auto-context of every turn — was hundreds of
    /// thousands of dictionary inserts each time.
    /// </summary>
    private volatile SearchSnapshot? _searchSnapshot;

    /// <summary>
    /// Searches the index for the most relevant chunks.
    /// </summary>
    /// <param name="queryEmbedding">Embedding of the search query; when <c>null</c> or empty, falls back to keyword search.</param>
    /// <param name="keywordFallback">Plaintext query used when semantic search is unavailable.</param>
    /// <param name="topK">Maximum number of results to return.</param>
    public async Task<List<RagHit>> SearchAsync(
        float[]? queryEmbedding,
        string?  keywordFallback,
        int      topK,
        CancellationToken ct)
    {
        // Snapshot the chunk list under the lock, then run the (potentially O(N)) similarity
        // computation OUTSIDE the lock so a large search never blocks concurrent re-indexing.
        List<RagChunk>  allChunks;
        SearchSnapshot? cached;
        int             version;
        await _chunkLock.WaitAsync(ct);
        try
        {
            if (_chunksByFile.Count == 0) return [];
            version   = _contentVersion;
            cached    = _searchSnapshot is { } s && s.Version == version ? s : null;
            allChunks = cached?.Chunks ?? _chunksByFile.Values.SelectMany(v => v).ToList();
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
        // include the chunk's type and relative path so name-based queries score strongly. The
        // tokens are cached per chunk (RagChunk.Bm25Tokens, invalidated per file by re-chunking) —
        // tokenizing the whole corpus on every query made each shadow pre-warm during typing O(N).
        List<(int Idx, double Score)> lexical = [];
        if (!string.IsNullOrWhiteSpace(keywordFallback))
        {
            var queryTokens = CodeTokenizer.Tokenize(keywordFallback);
            if (queryTokens.Count > 0)
            {
                // Built once per index version (the index is read-only once built, so concurrent
                // queries share it); a version published meanwhile simply gets its own on the next query.
                var bm25 = cached?.Lexical;
                if (bm25 is null)
                {
                    bm25 = new Bm25Index(allChunks.Select(c => c.Bm25Tokens).ToList());
                    _searchSnapshot = new SearchSnapshot(version, allChunks, bm25);
                }
                lexical = bm25.Rank(queryTokens, pool);
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
            // IsCosine says where THIS result came from: a purely lexical hit has no cosine, and
            // that is the only way to know — its score is 0f, just like a similarity of zero.
            return RagHit.FromFusion(fused, allChunks, cosByIdx, topK);
        }

        if (vector.Count > 0)
            return vector.Take(topK).Select(x => new RagHit(allChunks[x.Idx], x.Cos, IsCosine: true)).ToList();

        if (lexical.Count > 0)
            return lexical.Take(topK).Select(x => new RagHit(allChunks[x.Idx], (float)x.Score, IsCosine: false)).ToList();

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
            var generation = Volatile.Read(ref _indexGeneration);
            var embedding = await _client.GetEmbeddingAsync(query, model, ct);
            if (embedding is null) return;

            var results = await SearchAsync(embedding, query, Math.Max(1, _config.RagTopK), ct);

            // Publish all three values atomically via a single reference assignment — under the
            // lock that replaces the index, and only if no replacement happened meanwhile.
            await _chunkLock.WaitAsync(ct);
            try
            {
                if (generation == _indexGeneration)
                    _shadow = new ShadowCache(query, embedding, results);
            }
            finally { _chunkLock.Release(); }
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
    public (float[]? Embedding, List<RagHit>? Results) TryGetShadow(string query)
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

            // ── Watch for file changes — armed BEFORE the pass reads anything ─
            // The pass can run for minutes (embeddings); a file saved while it runs used to be
            // invisible until the next boot (the watcher was only armed after the final save).
            // Events raised during the pass accumulate in _pendingRebuild (OnDebounceElapsed
            // defers while IsIndexing) and are drained after the final SaveAsync below.
            SetupFileWatcher(rootDir);

            // ── Load existing index from disk ─────────────────────────────────
            var loaded = await db.LoadAsync(ct);
            // ⚠ Vectors from ANOTHER embedding model are not reusable: other dimensions give a cosine of
            // 0 everywhere, the same dimensions give noise — under a "✅". The model the stored vectors
            // came from is recorded with them (after SaveAsync below); a different model re-embeds.
            var embModel    = EmbeddingModel;
            var storedModel = await db.GetMetaAsync(EmbeddingModelMetaKey, ct);
            if (storedModel is not null && !string.Equals(storedModel, embModel, StringComparison.Ordinal))
                foreach (var c in loaded) c.Embedding = null;
            _indexedEmbeddingModel = embModel;
            // Replaced even when this root has nothing on disk yet: the service outlives its root,
            // and a first pass on a new workspace would otherwise serve the PREVIOUS workspace's
            // chunks for as long as it runs — and forever when the new one has no source file.
            await ApplyChunksAsync(loaded, replaceAll: true, ct);
            if (loaded.Count > 0)
                Status = $"RAG: {ChunkCount} chunks loaded (verifying changes…)";

            // ── Enumerate source files ────────────────────────────────────────
            // ⚠ What the pass DISCARDED, and why. The per-file catch below said "skip unreadable
            // files" and said it to nobody: that is what made a missing Roslyn chunker invisible for
            // six versions - every .cs in the workspace was dropped whole, and the index reported
            // itself ready. The cause was fixed in 1.6.6; the silence that hid it was not. A truly
            // unreadable file is ordinary, so we do not speak per file: we count, and say it ONCE at
            // the end of the pass with the first exception seen - the "n out of N" ratio is what
            // makes a systematic failure readable at a glance.
            var skipped      = 0;
            var firstSkipped = string.Empty;

            // ⚠ A walk that failed part-way is not a smaller project: the final replaceAll would drop every
            // file not listed yet, under a "✅". The index stays as loaded instead.
            if (EnumerateSourceFiles(rootDir) is not { } files)
            {
                Status = "RAG: could not list the project files — the index was left as it was (see /diagnostics).";
                return;
            }
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
                catch (Exception ex)
                {
                    // Still no PER-FILE trace: the loop sees thousands of them.
                    skipped++;
                    if (firstSkipped.Length == 0)
                        firstSkipped = $"{Path.GetFileName(files[fi])} — {ex.GetType().Name}: {ex.Message}";
                }

                // Refresh the in-memory index every 20 files (merge — keep the boot-loaded
                // entries for files not yet verified); the authoritative replace happens below.
                if (fi % 20 == 0 || fi == files.Count - 1)
                    await ApplyChunksAsync(newChunks, replaceAll: false, ct);

                // Small throttle every 5 files to avoid flooding Ollama
                if (fi % 5 == 4)
                    await Task.Delay(80, ct);
            }

            // ── Persist final index ───────────────────────────────────────────
            await ApplyChunksAsync(newChunks, replaceAll: true, ct);
            // A pass that found nothing to change writes nothing: every start of the editor runs this
            // pass, and rewriting every row and vector for the same content cost the whole index in
            // disk writes. A changed embedding model is always saved — with the embedding circuit
            // open, its cleared vectors would compare equal to vectors that were never recomputed.
            var modelChanged = !string.Equals(storedModel, embModel, StringComparison.Ordinal);
            if (modelChanged || !SameAsStored(loaded, newChunks))
                await db.SaveAsync(newChunks, ct);
            if (modelChanged)
                await db.SetMetaAsync(EmbeddingModelMetaKey, embModel, ct);
            // ⚠ Reported even when the pass "succeeds": a full index built on zero files read is
            // exactly the state that used to read as normal.
            if (skipped > 0)
                Diagnostics.Record("ProjectIndexService",
                    $"{skipped} of {files.Count} file(s) skipped while indexing; first: {firstSkipped}");

            // A chunk without a vector escapes the semantic half of the search until the next pass
            // (nothing computes it in between): the status that /index and search_codebase show
            // counts them, with the remedy.
            var unembedded = newChunks.Count(c => c.Embedding is not { Length: > 0 });
            if (unembedded > 0)
                Diagnostics.Record("ProjectIndexService",
                    $"{unembedded} of {newChunks.Count} chunk(s) without embedding; semantic search misses them until /index rebuild");
            var holeStatus = unembedded > 0
                ? $" ({unembedded} of {ChunkCount} chunks without embedding — semantic search misses them; run /index rebuild)"
                : string.Empty;

            var embStatus = _client.IsEmbeddingCircuitOpen ? " (embedding ⚠ circuit open, keyword fallback)" : string.Empty;
            Status = $"RAG: ✅ {ChunkCount} chunks from {files.Count} files{holeStatus}{embStatus}";

            // ── Drain the backlog accumulated during the pass ─────────────────
            // A file modified after the pass read it entered the authoritative replaceAll above
            // with its OLD content; the watcher caught the change, so re-read those files now that
            // nothing can overwrite the result. Changes arriving during this drain re-arm the
            // debounce timer and are handled through the normal watcher path afterwards.
            string[] backlog;
            lock (_pendingRebuild)
            {
                backlog = [.. _pendingRebuild.Where(p => BelongsTo(p, rootDir))];
                _pendingRebuild.Clear();
            }
            if (backlog.Length > 0)
                await ReIndexFilesAsync(backlog, rootDir, ct);
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

    /// <summary>
    /// Whether a pass produced exactly the chunks it loaded: same files, lines, content and type name,
    /// and the very same embedding arrays — a reused vector is the loaded instance, a recomputed one
    /// never is. Compared as a multiset: a file can repeat a chunk verbatim.
    /// </summary>
    internal static bool SameAsStored(IReadOnlyList<RagChunk> stored, IReadOnlyList<RagChunk> pass)
    {
        if (stored.Count != pass.Count) return false;

        var remaining = new Dictionary<(string, string, int, int, string, string?, float[]?), int>();
        foreach (var chunk in stored)
        {
            var key = Key(chunk);
            remaining[key] = remaining.TryGetValue(key, out var n) ? n + 1 : 1;
        }
        foreach (var chunk in pass)
        {
            var key = Key(chunk);
            if (!remaining.TryGetValue(key, out var n) || n == 0) return false;
            remaining[key] = n - 1;
        }
        return true;

        static (string, string, int, int, string, string?, float[]?) Key(RagChunk c) =>
            (c.FilePath, c.RelPath, c.StartLine, c.EndLine, c.ContentHash, c.TypeName, c.Embedding);
    }

    /// <summary>
    /// Whether a queued change belongs to <paramref name="root"/>.
    /// </summary>
    /// <remarks>
    /// The service outlives its root: a change queued under the previous workspace, drained by the
    /// next workspace's pass, was chunked and written into the NEXT workspace's store. The check is
    /// the sandbox's own (<see cref="Inferpal.Services.Tools.PathSanitizer.AssertUnderRoot"/>): the watcher can report a
    /// path through a different link than the root was given with (<c>/private/var</c> for
    /// <c>/var</c> on macOS), and a plain prefix would drop legitimate changes there.
    /// </remarks>
    private static bool BelongsTo(string path, string root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        try
        {
            Inferpal.Services.Tools.PathSanitizer.AssertUnderRoot(path, root);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ── File watching ──────────────────────────────────────────────────────────

    /// <summary>The root <see cref="_watcher"/> is currently watching, if any.</summary>
    private string? _watchedRoot;

    /// <summary>
    /// Arms the watcher on <paramref name="rootDir"/>, unless it is already watching it.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Idempotent by root because it is now armed from two places</b>: the indexing pass, which
    /// needs it before reading anything, and <see cref="SetRoot"/>, which the front-ends call on
    /// every heartbeat. Re-creating the watcher on each of those would drop the events raised
    /// between the dispose and the next one — a save landing exactly there would be lost.
    /// </remarks>
    private void SetupFileWatcher(string rootDir)
    {
        if (_disposed) return;
        if (_watcher is not null && string.Equals(_watchedRoot, rootDir, StringComparison.OrdinalIgnoreCase))
            return;

        _watcher?.Dispose();
        _watcher     = null;
        _watchedRoot = null;
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
            _watchedRoot = rootDir;
        }
        catch { /* file watching is best-effort */ }
    }

    private void OnFileChanged(object _, FileSystemEventArgs e) => OnFileChangedCore(e.FullPath);

    internal void OnFileChangedCore(string path)
    {
        // The C# semantic index is cached per workspace, so something has to keep it honest: a
        // saved file can add or remove a reference, and a cache nobody invalidates answers about
        // code that no longer exists. 4 ms per file, unlike the RAG re-embedding below.
        // ⚠ No watcher exists when RAG is disabled — see CSharpSemanticIndex.ForWorkspace.
        // Same exclusions as the full pass, and BEFORE the semantic index hears of the change: every
        // agent write creates a .inferpal/history snapshot, which became a second copy of the class.
        if (IsExcluded(path)) return;

        Lsp.CSharpSemanticIndex.NotifyFileChanged(path);

        // ⚠ The RAG half only when a pass owns this root. Since SetRoot arms the watcher, events now
        // arrive with `ragEnabled` off as well — and chunking a file into an index that does not
        // exist, then writing its rows to SQLite, is work the user turned off. `IndexedRoot` rather
        // than `_config.RagEnabled`: `/index rebuild` indexes on demand whatever the setting says,
        // and that index must keep following its files.
        if (string.IsNullOrEmpty(IndexedRoot)) return;

        lock (_pendingRebuild) _pendingRebuild.Add(path);

        ArmDebounce();
    }

    // Debounce delay — wait this long after the last change before re-indexing.
    // Internal-settable so the watcher lifecycle tests don't wait 5 s per event.
    internal int DebounceMs { get; set; } = 5_000;

    /// <summary>(Re-)arms the debounce timer; no-op after dispose.</summary>
    private void ArmDebounce()
    {
        lock (_timerLock)
        {
            if (_disposed) return;
            _debounceTimer?.Dispose();
            _debounceTimer = new System.Threading.Timer(
                OnDebounceElapsed, null,
                dueTime: DebounceMs, period: System.Threading.Timeout.Infinite);
        }
    }

    private void OnDebounceElapsed(object? _)
    {
        // While an indexing pass runs, defer: ReIndexFilesAsync would race the pass's final
        // replaceAll, which silently overwrites whatever the re-index just published. Keep the
        // backlog and re-arm — the pass drains it right after its final SaveAsync; this retry
        // only matters if the pass dies (cancelled, backend error) before reaching the drain.
        if (IsIndexing)
        {
            ArmDebounce();
            return;
        }

        var root = RootDir;
        string[] pending;
        lock (_pendingRebuild)
        {
            pending = [.. _pendingRebuild.Where(p => BelongsTo(p, root))];
            _pendingRebuild.Clear();
        }
        if (pending.Length == 0 || string.IsNullOrEmpty(root) || _disposed) return;

        // Capture the token here, while the CTS is guaranteed alive: reading _cts.Token inside the
        // detached task would throw ObjectDisposedException if shutdown won the race, and that
        // exception would vanish into an unobserved task.
        CancellationToken ct;
        try { ct = _cts?.Token ?? CancellationToken.None; }
        catch (ObjectDisposedException) { return; }

        _ = Task.Run(async () =>
        {
            try { await ReIndexFilesAsync(pending, root, ct); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Diagnostics.Swallow("ProjectIndexService.ReIndexFiles", ex); }
        });
    }

    /// <summary>The <c>meta</c> key recording which model produced the stored vectors.</summary>
    private const string EmbeddingModelMetaKey = "embedding_model";

    /// <summary>The embedding model the in-memory vectors came from (set by the last pass).</summary>
    private volatile string _indexedEmbeddingModel = string.Empty;

    /// <summary>Bumped under <see cref="_chunkLock"/> whenever the published chunks change.</summary>
    private int _contentVersion;

    /// <summary>
    /// Serialises the watcher's re-index tasks. Each one reads a file, waits for the chat to go
    /// idle, then publishes: two tasks for the same file both waited, and the one holding the OLDER
    /// content could publish last — the index then kept a version of the file that no longer existed.
    /// </summary>
    private readonly SemaphoreSlim _reindexGate = new(1, 1);

    private async Task ReIndexFilesAsync(string[] changedFiles, string rootDir, CancellationToken ct)
    {
        await _reindexGate.WaitAsync(ct);
        try { await ReIndexFilesCoreAsync(changedFiles, rootDir, ct); }
        finally { _reindexGate.Release(); }
    }

    private async Task ReIndexFilesCoreAsync(string[] changedFiles, string rootDir, CancellationToken ct)
    {
        var db       = new RagDatabase(rootDir);
        var embModel = EmbeddingModel;
        // The vectors in memory came from the last pass's model: a model changed since cannot reuse them.
        var canReuse = string.Equals(embModel, _indexedEmbeddingModel, StringComparison.Ordinal);

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
                    _contentVersion++;
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

                // Reuse embeddings for unchanged chunks (same start line + content hash),
                // mirroring the initial pass: a save without content change — and the
                // post-pass backlog drain, which re-reads files the pass just embedded —
                // would otherwise re-embed the whole file for nothing.
                List<RagChunk>? previous = null;
                if (canReuse)
                {
                    await _chunkLock.WaitAsync(ct);
                    try
                    {
                        if (_chunksByFile.TryGetValue(file, out var current))
                            previous = [.. current];
                    }
                    finally { _chunkLock.Release(); }
                }

                foreach (var chunk in fileChunks)
                {
                    var existing = previous?.FirstOrDefault(c =>
                        c.StartLine == chunk.StartLine &&
                        c.ContentHash == chunk.ContentHash);
                    if (existing?.Embedding is { Length: > 0 })
                    {
                        chunk.Embedding = existing.Embedding;
                        continue;
                    }

                    // ⚠ Skip only the network call. A `break` here also skipped the REUSE of every later
                    // chunk: with the circuit open (or RAG turned off, the watcher still armed) the
                    // unchanged chunks of a saved file lost their vector, in memory and in SQLite.
                    if (!_config.RagEnabled || _client.IsEmbeddingCircuitOpen) continue;
                    // Yield the shared Ollama backend to any in-flight interactive request.
                    await GpuScheduler.WaitForChatIdleAsync(ct);
                    chunk.Embedding = await _client.GetEmbeddingAsync(chunk.Content, embModel, ct);
                }

                // Update memory — O(1) dict assignment replaces old chunks for this file
                await _chunkLock.WaitAsync(ct);
                try
                {
                    _chunksByFile[file] = fileChunks;
                    ChunkCount = _chunksByFile.Values.Sum(l => l.Count);
                    _contentVersion++;
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

    /// <param name="chunks">Chunks to publish to the in-memory index.</param>
    /// <param name="replaceAll">
    /// <c>true</c> replaces the whole index (authoritative end-of-pass state, drops deleted files);
    /// <c>false</c> merges per file — used for the periodic mid-pass refresh, so the index loaded
    /// from SQLite at boot keeps serving files the verify pass has not reached yet instead of
    /// being wiped after the first batch.
    /// </param>
    private async Task ApplyChunksAsync(List<RagChunk> chunks, bool replaceAll, CancellationToken ct)
    {
        await _chunkLock.WaitAsync(ct);
        try
        {
            if (replaceAll)
            {
                _chunksByFile.Clear();
                _indexGeneration++;
                _shadow = null;
            }
            foreach (var group in chunks.GroupBy(c => c.FilePath, StringComparer.OrdinalIgnoreCase))
                _chunksByFile[group.Key] = [.. group];
            ChunkCount = _chunksByFile.Values.Sum(l => l.Count);
            _contentVersion++;   // the search snapshot is stale from here
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

    private bool IsExcluded(string path) => IndexExclusions.IsExcluded(path, RootDir, _profileExcludes);

    /// <summary>
    /// Collects source files under <paramref name="rootDir"/>, skipping generated
    /// artifacts (bin, obj, .git, node_modules, .vs, .inferpal), whatever the project profile
    /// adds (<c>.inferpal/project.json</c>), and oversized files.
    /// </summary>
    /// <returns>The files, or <c>null</c> when the walk itself failed part-way — a partial list must
    /// not reach the pass's final replaceAll.</returns>
    private List<string>? EnumerateSourceFiles(string rootDir)
    {
        var result = new List<string>();
        // ⚠ Once per pass, BEFORE the walk: `IgnoreInaccessible` skips an unlistable folder without
        // throwing, so the `catch` below never sees it and the `null` it returns — "partial list,
        // does not replace the index" — does not fire either. The pass is legitimate (nothing better
        // can be done), but it has to SAY so: this is the only trace that a whole folder is missing
        // from the index, and it survives on disk.
        SkippedFolder = WorkspaceScan.FirstWalkGap(rootDir, rootDir);
        try
        {
            // ONE walk filtered by extension: the per-extension loop walked the whole tree once for
            // every supported language.
            foreach (var f in WorkspaceScan.EnumerateFiles(rootDir, "*"))
            {
                if (!CodeChunker.SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)
                    || IsExcluded(f))
                    continue;
                try
                {
                    if (new FileInfo(f).Length < CodeChunker.MaxFileSizeBytes) result.Add(f);
                }
                // ⚠ Per file: a file deleted between the walk and the stat (a git pull, a generator) threw
                // out of the WHOLE enumeration, and the pass replaced the index with what had been listed.
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Diagnostics.Swallow("ProjectIndexService.CollectFile", ex);
                }
            }
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("ProjectIndexService.CollectFiles", ex);
            return null;
        }
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
        // Flag first: a watcher callback already in flight must not resurrect the timer behind us.
        _disposed = true;

        lock (_timerLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        _cts?.Dispose();
        _watcher?.Dispose();
        _watchedRoot = null;

        // Deliberately NOT disposing _chunkLock/_shadowLock: a background indexing pass may still
        // be awaiting them, and disposing a SemaphoreSlim under a waiter turns a clean cancellation
        // into an ObjectDisposedException in a detached task. They die with the process.
    }
}
