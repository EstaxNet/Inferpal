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
    /// <summary>The client of code search, asked at each use: the embedding server can change in the settings.</summary>
    private readonly Func<IInferenceProvider> _clientOf;
    private IInferenceProvider _client => _clientOf();
    /// <summary>The client this index embeds with — its queries must be embedded by the same server (the embedding
    /// server's, <see cref="RoleClients"/>).</summary>
    internal IInferenceProvider SearchClient => _client;

    private readonly InferpalConfig     _config;
    private readonly LspSemanticProvider   _lsp;

    // Keyed by file path — O(1) file-targeted ops vs O(N) linear scan. ⚠ The file system's case rule (PathComparer), never
    // a fold of its own: on Linux `Foo.cs` renamed `foo.cs` is two paths, and folded, the rename's second half collapses
    // into its first — the old name is forgotten as deleted, the new one never indexed.
    private readonly Dictionary<string, List<RagChunk>> _chunksByFile = new(PathComparer.Default);
    private readonly SemaphoreSlim _chunkLock = new(1, 1);
    private readonly HashSet<string>     _pendingRebuild = new(PathComparer.Default);

    /// <summary>
    /// Source files the watcher saw change and that the index still holds in their previous version.
    /// </summary>
    /// <remarks>
    /// ⚠ Not <see cref="_pendingRebuild"/>: that set empties when the re-index STARTS, and the re-index
    /// then waits — for the GPU, which an agent holds for its whole turn. A file the agent writes is
    /// therefore out of the index until the turn ends, and a search answered about the version from
    /// before, or "nothing found" about a class just created, without a word. Leaves this set only
    /// once its new chunks are published (or its removal is).
    /// </remarks>
    private readonly HashSet<string> _notYetReindexed = new(PathComparer.Default);

    /// <summary>Changed source files the index has not caught up with yet, sorted.</summary>
    public IReadOnlyList<string> NotYetReindexed
    {
        get { lock (_notYetReindexed) return [.. _notYetReindexed.Order(StringComparer.Ordinal)]; }
    }

    private void CaughtUp(string path)
    {
        lock (_notYetReindexed) _notYetReindexed.Remove(path);
    }

    private CancellationTokenSource?    _cts;
    private FileSystemWatcher?          _watcher;
    private FileSystemWatcher?          _dirWatcher;   // folders: see SetupFileWatcher
    private FileSystemWatcher?          _rootWatcher;  // the root's own files — its ignore files: see SetupFileWatcher
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
    // ⚠ The results hold CHUNKS, which go stale when a file is re-indexed — the query's vector does not. Version is the
    // content version the search ran against: once it moves, the results are no longer served (the vector still is).
    private sealed record ShadowCache(
        string Query, float[] Embedding, List<RagHit> Results, int Version);

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

    /// <summary>The running pass's progress: files read, out of the files it lists (0 of 0 while it loads).</summary>
    internal (int Done, int Total) PassProgress => (_progressDone, _progressTotal);

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

    /// <summary>
    /// How many source files the last pass dropped because they are larger than
    /// <see cref="CodeChunker.MaxFileSizeBytes"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ The same argument as <see cref="SkippedFolder"/>, and the same reason it must be said
    /// HERE rather than left to a tool: the index is persisted, so a file over the cap stays
    /// invisible to <c>search_codebase</c> and to the per-turn auto-context across restarts, while
    /// the chunk count reads as complete — a dropped file is missing from every total, not
    /// subtracted from one.
    /// ⚠ The rule was already written next door and held by one of its two sites:
    /// <c>rename_symbol</c> folds its own oversize drops into its <c>ScanCoverage</c> under the
    /// comment <i>"what was NOT looked at travels with the result, like in every other scanning
    /// tool"</i>. The pass was the other scanning tool.
    /// </remarks>
    public int SkippedBySize { get; private set; }

    /// <summary>The files <see cref="SkippedBySize"/> counts, relative to the root, at most <see cref="OversizeNamesCap"/>:
    /// the settings card can name them, so the user can split or exclude one on purpose.</summary>
    public IReadOnlyList<string> OversizeFiles { get; private set; } = [];

    /// <summary>How many names <see cref="OversizeFiles"/> keeps; the count stays exact beyond it.</summary>
    internal const int OversizeNamesCap = 50;

    /// <summary>
    /// How many source files the last pass left out because the workspace's ignore files exclude them, and the rules
    /// that did (<c>.aiignore line 3 (`secrets/`)</c>) — empty when none.
    /// </summary>
    /// <remarks>
    /// ⚠ Said by <c>search_codebase</c> for the reason <see cref="SkippedBySize"/> is: a file missing from the index is
    /// missing from every count, and "nothing found" about it reads as "it does not exist".
    /// </remarks>
    public IgnoredSourceFiles ExcludedByIgnoreFiles { get; private set; } = IgnoredSourceFiles.None;

    /// <summary>Solution root directory being indexed.</summary>
    public string RootDir    { get; private set; } = string.Empty;

    /// <summary>The root the last indexing pass was started on (empty = never). <see cref="RootDir"/> can be
    /// pinned without indexing, so it does not say whether a pass ever ran.</summary>
    public string IndexedRoot { get; private set; } = string.Empty;

    // Extra exclusion patterns contributed by .inferpal/project.json. Read when the
    // root is pinned rather than per file: this sits in the enumeration loop. Additive only — the
    // profile can lengthen the built-in list, never shorten it (see IndexExclusions).
    private volatile IReadOnlyList<string> _profileExcludes = [];

    /// <summary>The exclusion patterns of <c>.inferpal/project.json</c> the index applies — what the settings page lists.</summary>
    public IReadOnlyList<string> ProfileExcludes => _profileExcludes;

    // ── Construction ──────────────────────────────────────────────────────────

    /// <param name="searchClient">The client of code search, when it is not <paramref name="client"/>: the embedding
    /// server's (<see cref="RoleClients"/>).</param>
    public ProjectIndexService(IInferenceProvider client, InferpalConfig config, LspSemanticProvider lsp,
                               Func<IInferenceProvider>? searchClient = null)
    {
        _clientOf = searchClient ?? (() => client);
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
        Pin(rootDir);
        // ⚠ The watcher is armed HERE too, not only by the indexing pass, because it has a second
        // consumer that has nothing to do with RAG: the C# semantic index is cached per workspace
        // and this watcher is the only thing that invalidates it. Armed from the pass alone, it did
        // not exist at all when `ragEnabled` is off — so `analyze_impact` answered its
        // compiler-resolved section, the one it tells the model to trust over the name-matching
        // ones, from a compilation frozen at the first C# analysis of the session. `SetRoot` is
        // what both front-ends call with RAG on or off, which is exactly the property needed.
        SetupFileWatcher(rootDir);
    }

    /// <summary>What pinning a root is, for <see cref="SetRoot"/> and <see cref="StartIndexing"/> alike.</summary>
    private void Pin(string rootDir)
    {
        RootDir          = rootDir;
        _profileExcludes = ProjectProfile.Load(rootDir).IndexExcludes;
        // The workspace's ignore files apply below it, to everything that reads for the model (ContentExclusions). Both
        // front-ends and every ACP thread set their root here: one declaration per workspace.
        ContentExclusions.Declare(rootDir, () => _config.IgnoreFiles);
        FileHistoryService.IgnoreExistingHistory(rootDir);
    }

    /// <summary>
    /// Starts a background indexing pass over all source files under <paramref name="rootDir"/>.
    /// If a previous pass is running it is cancelled before the new one begins.
    /// </summary>
    /// <remarks>
    /// ⚠ The index lives under the user's application data. The repository's <c>.gitignore</c> is the user's file and is
    /// never rewritten — the file history ignores itself from inside its own folder, the one place this writes to, and
    /// only when that folder already exists (<see cref="FileHistoryService.IgnoreExistingHistory"/>): a front-end may
    /// start a pass without <see cref="SetRoot"/>.
    /// </remarks>
    public void StartIndexing(string rootDir)
    {
        // ⚠ Pinned as SetRoot pins it: a front-end may start a pass on a root it never set, and a root whose ignore files
        // were never declared excludes nothing — everything under it read for the model.
        Pin(rootDir);
        IndexedRoot = rootDir;
        // Language servers started for another workspace would otherwise run until the editor closes.
        _lsp.ReleaseSessionsExcept(rootDir);

        // ⚠ The pass before and the pass started are swapped as one: two callers at once (the start's retry, a send's pin)
        // each saw no pass before them, and two passes ran together — the first one's end said "stopped" mid-way through
        // the second, and the watcher stopped deferring to it.
        lock (_passGate)
        {
            var previousCts  = _cts;
            var previousPass = _indexingPass;
            try { previousCts?.Cancel(); } catch (ObjectDisposedException) { }

            var cts   = new CancellationTokenSource();
            var token = cts.Token;   // captured now: the task must not read a field a later call replaces
            _cts    = cts;
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
    }

    /// <summary>Held while a pass replaces the one before it (<see cref="StartIndexing"/>).</summary>
    private readonly object _passGate = new();

    /// <summary>The indexing pass in flight (or last run); a new pass waits for it to end.</summary>
    private Task? _indexingPass;

    /// <summary>
    /// Shows the index saved for <paramref name="rootDir"/> without indexing it: no pass, no re-indexing on a change,
    /// nothing written — not even an empty database. For a host that only SHOWS the index (the settings window), never
    /// one a chat searches.
    /// </summary>
    /// <remarks>⚠ A pass here walked and embedded the folder the window was opened in — the whole home folder for a
    /// window started from there —, and a window opened from a thread ran a second indexer, in the same process, on the
    /// thread's database. Not a pass either way: shown as saved, brought up to date by the chat that uses it.</remarks>
    public async Task ShowSavedAsync(string rootDir, CancellationToken ct)
    {
        var file = RagDatabase.PathFor(rootDir);
        if (!File.Exists(file)) return;
        // ⚠ Said while it reads: a large index takes seconds, and until then the card read "not indexed yet" over an
        // index that exists — its Build button the very second indexer this method is here to avoid.
        _loadingSaved = true;
        try
        {
            var db     = new RagDatabase(rootDir);
            var loaded = await db.LoadAsync(ct).ConfigureAwait(false);
            if (loaded.Count == 0) return;
            var model  = await db.GetMetaAsync(EmbeddingModelMetaKey, ct).ConfigureAwait(false);
            // A pass started meanwhile (the page's Rebuild) owns the index from here.
            if (IsIndexing || !string.IsNullOrEmpty(IndexedRoot)) return;
            await ApplyChunksAsync(loaded, replaceAll: true, ct).ConfigureAwait(false);
            _indexedEmbeddingModel = model ?? string.Empty;
            // In WAL mode the latest writes may still sit in the -wal file.
            var wal = file + "-wal";
            var at  = File.GetLastWriteTime(file);
            _savedAt = File.Exists(wal) && File.GetLastWriteTime(wal) > at ? File.GetLastWriteTime(wal) : at;
            _showsSaved = true;
        }
        finally { _loadingSaved = false; }
    }

    /// <summary>The index shown is the one saved (<see cref="ShowSavedAsync"/>), not one a pass of this service built.</summary>
    private volatile bool _showsSaved;
    private DateTime? _savedAt;

    /// <summary><see cref="ShowSavedAsync"/> is reading the saved index.</summary>
    private volatile bool _loadingSaved;

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
            vector = VectorMath.RankBySimilarity(
                allChunks, c => c.Embedding, queryEmbedding, _config.RagSimilarityThreshold, pool);
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

        // ⚠ A file an ignore file excludes since it was indexed (a saved index read at start, a rule added while the
        // re-index waits) is never returned: dropped from both rankings, counted for the note of the call.
        vector  = vector.Where(x => !ContentExclusions.SkipInWalk(allChunks[x.Idx].FilePath)).ToList();
        lexical = lexical.Where(x => !ContentExclusions.SkipInWalk(allChunks[x.Idx].FilePath)).ToList();

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
    public async Task ShadowPreWarmAsync(string query, CancellationToken ct)
    {
        if (ChunkCount == 0 || _client.IsEmbeddingCircuitOpen || QueryEmbeddingModel is not { } model) return;

        // Non-blocking: skip if another pre-warm is already running
        if (!await _shadowLock.WaitAsync(0, ct)) return;
        try
        {
            var generation = Volatile.Read(ref _indexGeneration);
            var embedding = await EmbeddingModels.EmbedCodeQueryAsync(_client, model, query, ct);
            if (embedding is null) return;

            // Read BEFORE the search: a file re-indexed while it runs leaves results that are already stale.
            var version = Volatile.Read(ref _contentVersion);
            var results = await SearchAsync(embedding, query, Math.Max(1, _config.RagTopK), ct);

            // Publish all three values atomically via a single reference assignment — under the
            // lock that replaces the index, and only if no replacement happened meanwhile.
            await _chunkLock.WaitAsync(ct);
            try
            {
                if (generation == _indexGeneration)
                    _shadow = new ShadowCache(query, embedding, results, version);
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
    /// The results are <c>null</c> once a file has been re-indexed since they were computed: the embedding is
    /// still returned, and the caller searches again with it.
    /// </summary>
    public (float[]? Embedding, List<RagHit>? Results) TryGetShadow(string query)
    {
        // Single volatile read — the captured reference is immutable, so no tearing.
        var shadow = _shadow;
        if (shadow is not null && string.Equals(shadow.Query, query, StringComparison.OrdinalIgnoreCase))
            return (shadow.Embedding, shadow.Version == Volatile.Read(ref _contentVersion) ? shadow.Results : null);
        return (null, null);
    }

    // ── Indexing loop ──────────────────────────────────────────────────────────

    private async Task RunIndexingAsync(string rootDir, CancellationToken ct)
    {
        IsIndexing     = true;
        _showsSaved    = false;
        _passCompleted = false;   // a re-index must not write "✅" over a pass that is running, or failed
        _failure       = null;
        _stopped       = false;
        (_progressDone, _progressTotal) = (0, 0);
        Status         = "RAG: starting indexer…";
        // A full pass reads every file again; a change made DURING it re-enters through the watcher.
        lock (_notYetReindexed) _notYetReindexed.Clear();

        try
        {
            var db = new RagDatabase(rootDir);

            // ── Watch for file changes — armed BEFORE the pass reads anything ─
            // The pass can run for minutes (embeddings), and a watcher armed only after the final
            // save leaves a file saved meanwhile invisible until the next boot. Events raised during
            // the pass accumulate in _pendingRebuild (OnDebounceElapsed defers while IsIndexing) and
            // are drained after the final SaveAsync below.
            SetupFileWatcher(rootDir);

            // ── Load existing index from disk ─────────────────────────────────
            var loaded = await db.LoadAsync(ct);
            // ⚠ Vectors from ANOTHER embedding model are not reusable: other dimensions give a cosine of
            // 0 everywhere, the same dimensions give noise — under a "✅". The model the stored vectors
            // came from is recorded with them (after SaveAsync below); a different model re-embeds. So does
            // another DOCUMENT format for the same model (EmbeddingModels.CodeIndexIdentity). No model at all (none
            // configured, none installed) is an index on its keyword half: nothing is embedded.
            var storedModel = await db.GetMetaAsync(EmbeddingModelMetaKey, ct);
            // With semantic search off nothing is chosen: the stored vectors stay what they are, for when it is back on.
            var previous    = EmbeddingModels.ModelOfIdentity(storedModel);
            string? embModel;
            if (_config.RagEnabled)
                (embModel, _passedOver) = await EmbeddingModels.ChooseForCodeAsync(_config, _client, previous, ct);
            else
                (embModel, _passedOver) = (EmbeddingModels.Configured(_config) ?? previous, []);
            var embIdentity = EmbeddingModels.CodeIndexIdentity(embModel);
            if (storedModel is not null && !string.Equals(storedModel, embIdentity, StringComparison.Ordinal))
                foreach (var c in loaded) c.Embedding = null;
            _indexedEmbeddingModel = embIdentity;
            // ⚠ The pass saves its new vectors as it goes (below). Vectors of the PREVIOUS model left in the store under
            // the new model's name would be read back as valid by the next start if this pass were interrupted: a
            // changed model clears the stored vectors and records its name before anything else is written.
            var modelChanged = !string.Equals(storedModel, embIdentity, StringComparison.Ordinal);
            if (modelChanged)
            {
                await db.ClearEmbeddingsAsync(ct);
                await db.SetMetaAsync(EmbeddingModelMetaKey, embIdentity, ct);
            }
            // Replaced even when this root has nothing on disk yet: the service outlives its root,
            // and a first pass on a new workspace would otherwise serve the PREVIOUS workspace's
            // chunks for as long as it runs — and forever when the new one has no source file.
            await ApplyChunksAsync(loaded, replaceAll: true, ct);
            if (loaded.Count > 0)
                Status = $"RAG: {ChunkCount} chunks loaded (verifying changes…)";

            // ── Enumerate source files ────────────────────────────────────────
            // ⚠ What the pass DISCARDED, and why. A per-file catch that says "skip unreadable
            // files" and says it to nobody is what makes a systematic failure invisible: a missing
            // Roslyn chunker drops every .cs in the workspace, and the index reports itself ready.
            // A truly unreadable file is ordinary, so we do not speak per file: we count, and say it
            // ONCE at the end of the pass with the first exception seen — the "n out of N" ratio is
            // what makes the difference readable at a glance.
            var skipped      = 0;
            var firstSkipped = string.Empty;

            // ⚠ A walk that failed part-way is not a smaller project: the final replaceAll would drop every
            // file not listed yet, under a "✅". The index stays as loaded instead.
            if (EnumerateSourceFiles(rootDir) is not { } files)
            {
                Status   = "RAG: could not list the project files — the index was left as it was (see /diagnostics).";
                _failure = "could not list the project files";
                return;
            }
            if (files.Count == 0)
            {
                Status    = "RAG: no source files found.";
                IsIndexing = false;
                return;
            }

            // Build lookup of existing chunks by file path for incremental updates
            var existingByPath = new Dictionary<string, List<RagChunk>>(PathComparer.Default);
            foreach (var c in loaded)
            {
                if (!existingByPath.TryGetValue(c.FilePath, out var list))
                    existingByPath[c.FilePath] = list = [];
                list.Add(c);
            }

            var newChunks  = new List<RagChunk>(loaded.Count + 64);
            // Files whose chunks got NEW vectors since the last write, saved by batches. Saved only at the end, a first
            // pass interrupted by the editor closing or the host restarting loses every vector it computed, and on a
            // machine that embeds slowly a large repository never finishes its first index.
            var unsaved = new List<(string FilePath, IReadOnlyList<RagChunk> Chunks)>();
            // Files the pass could not read: kept as they were, then read again with the backlog.
            var unread = new List<string>();
            var applied     = 0;       // newChunks[..applied] are in the in-memory index already
            var askedServer = false;   // an embedding was asked since the last throttle point

            for (int fi = 0; fi < files.Count; fi++)
            {
                ct.ThrowIfCancellationRequested();
                Status = $"RAG: {fi + 1}/{files.Count} — {Path.GetFileName(files[fi])}";
                (_progressDone, _progressTotal) = (fi, files.Count);

                try
                {
                    // Decoded like the file an edit rewrites: an indexed excerpt is code the model quotes back.
                    var content    = await Tools.TextFileEncoding.ReadTextAsync(files[fi], ct);
                    var fileChunks = await ChunkFileAsync(files[fi], content, rootDir, ct);
                    var embedded   = false;

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
                        if (_config.RagEnabled && embModel is not null && !_client.IsEmbeddingCircuitOpen)
                        {
                            // Yield the machine's graphics card to any in-flight interactive request — not a server elsewhere.
                            if (_client.SharesGpuWithChat) await GpuScheduler.WaitForChatIdleAsync(ct);
                            askedServer = true;
                            var emb = await EmbeddingModels.EmbedCodeDocumentAsync(_client, embModel, chunk.Content, ct);
                            chunk.Embedding = emb; // null if model unavailable
                            embedded |= emb is { Length: > 0 };
                        }

                        newChunks.Add(chunk);
                    }
                    if (embedded) unsaved.Add((files[fi], fileChunks));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Still no PER-FILE trace: the loop sees thousands of them.
                    skipped++;
                    if (firstSkipped.Length == 0)
                        firstSkipped = $"{Path.GetFileName(files[fi])} — {ex.GetType().Name}: {ex.Message}";
                    // ⚠ Unread is not gone: what the index held for it stays, said not caught up, and it is read again
                    // once the pass ends. Dropped by the replacement below, a file locked for a moment (a build writing
                    // it) lost every chunk it had — in memory and on disk — until its next change.
                    if (existingByPath.TryGetValue(files[fi], out var kept)) newChunks.AddRange(kept);
                    unread.Add(files[fi]);
                }

                // Refresh the in-memory index every 20 files (merge — keep the boot-loaded
                // entries for files not yet verified); the authoritative replace happens below.
                // ⚠ Only the chunks added since the last refresh: each file is read once, so the earlier ones are already
                // in place, and handing the whole list over every 20 files makes the pass quadratic (on a repository of
                // 140 000 files it falls from 48 to 30 files a second within the hour).
                if (fi % 20 == 0 || fi == files.Count - 1)
                {
                    await ApplyChunksAsync(newChunks.GetRange(applied, newChunks.Count - applied), replaceAll: false, ct);
                    applied = newChunks.Count;
                }

                if (unsaved.Count >= CheckpointFiles)
                {
                    await SaveProgressAsync(db, unsaved, ct);
                    unsaved.Clear();
                }

                // Small throttle every 5 files to avoid flooding the embedding server — only when it was asked: on the
                // keyword half nothing leaves the machine, and the pause is most of the time a file takes.
                if (fi % 5 == 4)
                {
                    if (askedServer) await Task.Delay(80, ct);
                    askedServer = false;
                }
            }

            // ── Persist final index ───────────────────────────────────────────
            await ApplyChunksAsync(newChunks, replaceAll: true, ct);
            // A pass that found nothing to change writes nothing: every start of the editor runs this
            // pass, and rewriting every row and vector for the same content cost the whole index in
            // disk writes. A changed embedding model is always saved — with the embedding circuit
            // open, its cleared vectors would compare equal to vectors that were never recomputed.
            if (modelChanged || !SameAsStored(loaded, newChunks))
                await db.SaveAsync(newChunks, ct);
            // ⚠ Reported even when the pass "succeeds": a full index built on zero files read
            // otherwise reads as normal.
            if (skipped > 0)
                Diagnostics.Record("ProjectIndexService",
                    $"{skipped} of {files.Count} file(s) skipped while indexing; first: {firstSkipped}");

            // A chunk without a vector escapes the semantic half of the search until the next pass
            // (nothing computes it in between): the status that /index and search_codebase show
            // counts them, with the remedy.
            var unembedded = newChunks.Count(c => c.Embedding is not { Length: > 0 });
            if (unembedded > 0 && embModel is not null)
                Diagnostics.Record("ProjectIndexService",
                    $"{unembedded} of {newChunks.Count} chunk(s) without embedding; semantic search misses them until /index rebuild");

            _passCompleted = true;
            _updatedAt     = DateTime.Now;
            Status = await ReadyStatusAsync(ct);

            // ── Drain the backlog accumulated during the pass ─────────────────
            // A file modified after the pass read it entered the authoritative replaceAll above
            // with its OLD content; the watcher caught the change, so re-read those files now that
            // nothing can overwrite the result. Changes arriving during this drain re-arm the
            // debounce timer and are handled through the normal watcher path afterwards.
            string[] backlog;
            lock (_notYetReindexed) foreach (var f in unread) _notYetReindexed.Add(f);
            lock (_pendingRebuild)
            {
                foreach (var f in unread) _pendingRebuild.Add(f);
                backlog = [.. _pendingRebuild.Where(p => BelongsTo(p, rootDir))];
                _pendingRebuild.Clear();
            }
            if (backlog.Length > 0)
                await ReIndexFilesAsync(backlog, rootDir, ct);
        }
        catch (OperationCanceledException)
        {
            Status   = "RAG: indexing cancelled.";
            _stopped = true;
        }
        catch (Exception ex)
        {
            // ⚠ The one failure that stops the whole pass, and it traced nothing: the support
            // bundle carried not a line of it. And `ex.Message` of a WRAPPER exception — which is
            // what a failure to load the native SQLite library produces — names nothing at all.
            Diagnostics.Swallow("ProjectIndexService.IndexingPass", ex);
            Status   = $"RAG: error — {Diagnostics.RootMessage(ex)}";
            _failure = Diagnostics.RootMessage(ex);
        }
        finally
        {
            IsIndexing = false;
        }
    }

    /// <summary>Files with new vectors written at once during a pass; the rest of the store keeps its rows.</summary>
    internal const int CheckpointFiles = 20;

    /// <summary>
    /// Writes the files a pass has embedded so far. A failure costs the progress only — the final save still writes
    /// the whole index — and is said once, by its exception type: a full disk fails at every batch.
    /// </summary>
    private static async Task SaveProgressAsync(RagDatabase db,
        IReadOnlyList<(string FilePath, IReadOnlyList<RagChunk> Chunks)> files, CancellationToken ct)
    {
        try { await db.SaveFilesAsync(files, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.RecordOnce("ProjectIndexService.SaveProgress",
                $"the index progress could not be saved during the pass ({ex.GetType().Name}: {ex.Message}); " +
                "an interrupted pass starts over, a finished one still saves everything", ex.GetType().Name);
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
        if (_watcher is not null && string.Equals(_watchedRoot, rootDir, PathComparer.Comparison))
            return;

        _watcher?.Dispose();
        _watcher     = null;
        _dirWatcher?.Dispose();
        _dirWatcher  = null;
        _rootWatcher?.Dispose();
        _rootWatcher = null;
        _watchedRoot = null;
        try
        {
            _watcher = new FileSystemWatcher(rootDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter          = NotifyFilters.LastWrite | NotifyFilters.FileName,
                InternalBufferSize    = WatcherBufferBytes,
                EnableRaisingEvents   = true,
            };
            foreach (var ext in CodeChunker.SupportedExtensions)
                _watcher.Filters.Add($"*{ext}");

            _watcher.Error   += OnWatcherError;
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
            _watcher.Renamed += (_, e) =>
            {
                OnFileChangedCore(e.OldFullPath);
                OnFileChangedCore(e.FullPath);
            };

            // ⚠ Folders too: the watcher above sees source files, and a folder renamed, moved or sent to the Recycle Bin
            // raises one event for the folder and none for the files inside. Without this second watcher both indexes
            // kept the old paths for the life of the process and never learned the new ones.
            _dirWatcher?.Dispose();
            _dirWatcher = new FileSystemWatcher(rootDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter          = NotifyFilters.DirectoryName,
                InternalBufferSize    = WatcherBufferBytes,
                EnableRaisingEvents   = true,
            };
            _dirWatcher.Error   += OnWatcherError;
            _dirWatcher.Created += (_, e) => OnDirectoryChangedCore(e.FullPath);
            _dirWatcher.Deleted += (_, e) => OnDirectoryChangedCore(e.FullPath);
            _dirWatcher.Renamed += (_, e) =>
            {
                OnDirectoryChangedCore(e.OldFullPath);
                OnDirectoryChangedCore(e.FullPath);
            };

            // ⚠ And the root's own files, whatever their extension: the watcher above filters on source extensions, so an
            // edited .aiignore raised no event and both indexes kept answering with what it now excludes.
            _rootWatcher = new FileSystemWatcher(rootDir)
            {
                IncludeSubdirectories = false,
                NotifyFilter          = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents   = true,
            };
            _rootWatcher.Error   += OnWatcherError;
            _rootWatcher.Changed += (_, e) => OnRootFileChanged(e.FullPath);
            _rootWatcher.Created += (_, e) => OnRootFileChanged(e.FullPath);
            _rootWatcher.Deleted += (_, e) => OnRootFileChanged(e.FullPath);
            _rootWatcher.Renamed += (_, e) =>
            {
                OnRootFileChanged(e.OldFullPath);
                OnRootFileChanged(e.FullPath);
            };
            _watchedRoot = rootDir;
        }
        catch { /* file watching is best-effort */ }
    }

    /// <summary>
    /// A folder under the root was created, removed or renamed: the C# index re-reads it, and every indexed file under
    /// its path — gone — and every source file under it now are queued like single-file changes.
    /// </summary>
    internal void OnDirectoryChangedCore(string dirPath)
    {
        // The C# index first, under its own exclusions only (see OnFileChangedCore).
        if (WorkspaceScan.IsExcludedPath(dirPath, RootDir)) return;
        Lsp.CSharpSemanticIndex.NotifyDirectoryChanged(dirPath);

        if (IsExcluded(dirPath) || string.IsNullOrEmpty(IndexedRoot)) return;
        _ = QueueDirectoryAsync(dirPath);
    }

    private async Task QueueDirectoryAsync(string dirPath)
    {
        try
        {
            var prefix = dirPath.TrimEnd('\\', '/');
            List<string> files;
            await _chunkLock.WaitAsync().ConfigureAwait(false);
            try
            {
                files = _chunksByFile.Keys
                    .Where(p => p.Length > prefix.Length && p.StartsWith(prefix, PathComparer.Comparison)
                                && p[prefix.Length] is '\\' or '/')
                    .ToList();
            }
            finally { _chunkLock.Release(); }

            if (Directory.Exists(dirPath))
                files.AddRange(WorkspaceScan.EnumerateFiles(dirPath, "*")
                    .Where(f => CodeChunker.SupportedExtensions.Contains(Path.GetExtension(f)) && !IsExcluded(f)));
            if (files.Count == 0) return;

            lock (_pendingRebuild) foreach (var f in files) _pendingRebuild.Add(f);
            lock (_notYetReindexed)
                foreach (var f in files.Where(f => CodeChunker.SupportedExtensions.Contains(Path.GetExtension(f))))
                    _notYetReindexed.Add(f);
            ArmDebounce();
        }
        catch (Exception ex) { Diagnostics.Swallow("ProjectIndexService.QueueDirectory", ex); }
    }

    private void OnFileChanged(object _, FileSystemEventArgs e) => OnFileChangedCore(e.FullPath);

    // The 8 KB default holds about sixty paths, fewer than a checkout or a build changes at once.
    private const int WatcherBufferBytes = 64 * 1024;

    private void OnWatcherError(object _, ErrorEventArgs e) => OnEventsLost(e.GetException());

    /// <summary>
    /// A watcher lost events — its buffer overflowed, or it failed — and no event says which files changed: both indexes
    /// read the whole root again (the search index keeps every vector whose chunk did not change).
    /// </summary>
    /// <remarks>⚠ Without it, a <c>git checkout</c> that changes a few hundred files leaves deleted files served as current
    /// and new ones never indexed, and <c>rename_symbol</c> refuses "index out of date" until the process ends.</remarks>
    internal void OnEventsLost(Exception cause)
    {
        Diagnostics.Swallow("ProjectIndexService.Watcher", cause);
        if (_watchedRoot is not { } root) return;
        // A watcher that FAILED raises nothing more (one that overflowed goes on): the next SetRoot arms a new one.
        if (cause is not InternalBufferOverflowException) _watchedRoot = null;
        // One reading at a time: a checkout overflows the buffers several times, and each would read the whole root.
        if (Interlocked.Exchange(ref _rereadQueued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            Volatile.Write(ref _rereadQueued, 0);   // what is lost from here on needs a reading of its own
            Lsp.CSharpSemanticIndex.NotifyExclusionsChanged(root);   // re-reads every file of the root
            if (!string.IsNullOrEmpty(IndexedRoot)) await QueueDirectoryAsync(root).ConfigureAwait(false);
        });
    }

    private int _rereadQueued;

    /// <summary>
    /// When <paramref name="path"/> is an ignore file of the workspace, every file is judged again — the indexed ones it now
    /// excludes leave the index (QueueDirectoryAsync queues every indexed file under the folder), the ones it no longer
    /// excludes enter it; the C# index too, RAG on or off (rename_symbol and the analysis read its trees).
    /// </summary>
    /// <returns>Whether it was one.</returns>
    internal bool OnRootFileChanged(string path)
    {
        if (!ContentExclusions.Changed(path)) return false;
        var root = RootDir;
        _ = Task.Run(() => Lsp.CSharpSemanticIndex.NotifyExclusionsChanged(root));
        if (!string.IsNullOrEmpty(IndexedRoot)) _ = Task.Run(() => RejudgeAsync(root));
        return true;
    }

    /// <summary>
    /// After an ignore file changed: the files whose verdict changed are queued — indexed ones it now excludes (the
    /// re-index forgets them), source files it no longer excludes and the index lacks (the re-index reads them) — and the
    /// count search_codebase says is taken again.
    /// </summary>
    /// <remarks>Only those: queueing the whole repository re-chunks every file at each save of an ignore file, and
    /// search_codebase then calls all of them "not re-indexed yet".</remarks>
    private async Task RejudgeAsync(string root)
    {
        try
        {
            HashSet<string> indexed;
            await _chunkLock.WaitAsync().ConfigureAwait(false);
            try { indexed = new HashSet<string>(_chunksByFile.Keys, PathComparer.Default); }
            finally { _chunkLock.Release(); }

            bool Source(string f) => CodeChunker.SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase);
            var listed = ContentExclusions.Counting(() => WorkspaceScan.EnumerateFiles(root, "*").ToList(), out var ignored);
            ExcludedByIgnoreFiles = IgnoredSourceFiles.Of(ignored.Only(Source));

            var changed = indexed.Where(f => ContentExclusions.Of(f) is not null)
                                 .Concat(listed.Where(f => Source(f) && !indexed.Contains(f) && !IsExcluded(f) && !IsOversize(f)))
                                 .ToList();
            if (changed.Count == 0) return;
            lock (_pendingRebuild) foreach (var f in changed) _pendingRebuild.Add(f);
            lock (_notYetReindexed) foreach (var f in changed) _notYetReindexed.Add(f);
            ArmDebounce();
        }
        catch (Exception ex) { Diagnostics.Swallow("ProjectIndexService.Rejudge", ex); }
    }

    internal void OnFileChangedCore(string path)
    {
        // The C# semantic index is cached per workspace, so something has to keep it honest: a
        // saved file can add or remove a reference, and a cache nobody invalidates answers about
        // code that no longer exists. 4 ms per file, unlike the RAG re-embedding below.
        // ⚠ No watcher exists when RAG is disabled — see CSharpSemanticIndex.ForWorkspace.
        // The workspace exclusions — the ones the C# compilation applies — BEFORE the semantic index hears of the change:
        // every agent write creates a .inferpal/history snapshot, which became a second copy of the class.
        // ⚠ Not the project profile's `indexExclude`: it narrows the SEARCH index only, and the compilation reads those
        // files. Told nothing, the C# index kept their old trees — rename_symbol refused "index out of date" on every
        // retry, or renamed part of the references, and analyze_impact answered about code that no longer existed.
        if (WorkspaceScan.IsExcludedPath(path, RootDir)) return;
        Lsp.CSharpSemanticIndex.NotifyFileChanged(path);
        if (OnRootFileChanged(path)) return;
        // The profile's exclusions: the search index below. The ignore files' are not: a file they exclude is
        // queued, for the re-index to take it out of the index.
        if (IndexExclusions.IsExcluded(path, RootDir, _profileExcludes)) return;

        // ⚠ The RAG half only when a pass owns this root. Since SetRoot arms the watcher, events now
        // arrive with `ragEnabled` off as well — and chunking a file into an index that does not
        // exist, then writing its rows to SQLite, is work the user turned off. `IndexedRoot` rather
        // than `_config.RagEnabled`: `/index rebuild` indexes on demand whatever the setting says,
        // and that index must keep following its files.
        if (string.IsNullOrEmpty(IndexedRoot)) return;

        lock (_pendingRebuild) _pendingRebuild.Add(path);
        if (CodeChunker.SupportedExtensions.Contains(Path.GetExtension(path)))
            lock (_notYetReindexed) _notYetReindexed.Add(path);

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

    /// <summary>
    /// The <c>meta</c> key recording which model — and which document format — produced the stored vectors
    /// (<see cref="EmbeddingModels.CodeIndexIdentity"/>).
    /// </summary>
    private const string EmbeddingModelMetaKey = "embedding_model";

    /// <summary>The <see cref="EmbeddingModels.CodeIndexIdentity"/> the in-memory vectors came from (set by the last pass).</summary>
    private volatile string _indexedEmbeddingModel = string.Empty;

    /// <summary>The installed embedding models the last pass's automatic choice passed over, for searching code worse
    /// than keywords alone — what the status names when the index runs on keywords.</summary>
    private volatile IReadOnlyList<string> _passedOver = [];

    /// <summary>The installed embedding models the last pass passed over (<see cref="EmbeddingModels.PassedOver"/>): every
    /// reader that says the index runs on keywords — its status, its card, <c>search_codebase</c> — names them, never
    /// "no embedding model installed" beside one that is.</summary>
    public IReadOnlyList<string> PassedOverEmbeddingModels => _passedOver;

    /// <summary>
    /// The model a query is embedded with: the one the index's vectors came from (chosen by the last pass), or
    /// <c>null</c> — keyword search only, because no embedding model is set or installed, the installed ones search code
    /// worse than keywords alone (<see cref="PassedOverEmbeddingModels"/>), or no pass has run yet.
    /// </summary>
    public string? QueryEmbeddingModel => _config.RagEnabled ? EmbeddingModels.ModelOfIdentity(_indexedEmbeddingModel) : null;

    /// <summary>Bumped under <see cref="_chunkLock"/> whenever the published chunks change.</summary>
    private int _contentVersion;

    /// <summary>Whether the last full pass completed — only then is the index "✅".</summary>
    /// <remarks>⚠ No file count is kept from the pass: the files a pass LISTED (those that failed included) were shown as
    /// "N files" until the next pass, while saves, deletions and moved folders changed the chunks under it. The count
    /// is read from the index as it is now (<see cref="FilesWithChunks"/>).</remarks>
    private bool _passCompleted;

    /// <summary>The files the index holds chunks of, now. Under <see cref="_chunkLock"/>.</summary>
    private int FilesWithChunks() => _chunksByFile.Count(kv => kv.Value.Count > 0);

    // What the settings card reads (Snapshot): the pass's progress, when the published index last changed, and how
    // the last pass ended when it did not complete. Status carries the same facts as an English sentence.
    private int       _progressDone, _progressTotal;
    private DateTime? _updatedAt;
    private string?   _failure;
    private bool      _stopped;

    /// <summary>The index as the settings card shows it: state, counts, holes, and what was left out.</summary>
    /// <remarks>Read under the chunk lock, like <see cref="ReadyStatusAsync"/>: the holes are counted on the index as
    /// it is now, re-indexed files included.</remarks>
    public async Task<IndexSnapshot> SnapshotAsync(CancellationToken ct)
    {
        int unembedded, files;
        await _chunkLock.WaitAsync(ct);
        try
        {
            unembedded = _chunksByFile.Values.Sum(l => l.Count(c => c.Embedding is not { Length: > 0 }));
            files      = FilesWithChunks();
        }
        finally { _chunkLock.Release(); }
        var saved = _showsSaved && !IsIndexing;
        return new IndexSnapshot(
            RootDir, IsIndexing, _progressDone, _progressTotal, _passCompleted, files, ChunkCount, unembedded,
            SkippedBySize, OversizeFiles, SkippedFolder, QueryEmbeddingModel, _client.IsEmbeddingCircuitOpen,
            saved ? _savedAt : _updatedAt, _failure, _stopped, saved, Loading: _loadingSaved && !IsIndexing,
            PassedOver: _passedOver);
    }

    /// <summary>
    /// The "✅" status, from the index as it is NOW: its chunks, the ones without a vector, the embedding breaker.
    /// </summary>
    /// <remarks>
    /// ⚠ Recomposed after every incremental re-index too, not only at the end of a full pass: a file saved while the
    /// embedding model did not answer loses its vectors — the file just edited, out of the semantic half of the
    /// search until the next start — and the pass's count said "✅" over it; a save that re-embeds a hole left the
    /// status still telling the user to run /index rebuild.
    /// </remarks>
    private async Task<string> ReadyStatusAsync(CancellationToken ct)
    {
        int total, unembedded, files;
        await _chunkLock.WaitAsync(ct);
        try
        {
            total      = _chunksByFile.Values.Sum(l => l.Count);
            unembedded = _chunksByFile.Values.Sum(l => l.Count(c => c.Embedding is not { Length: > 0 }));
            files      = FilesWithChunks();
        }
        finally { _chunkLock.Release(); }

        // No embedding model is a choice, not a hole: "run /index rebuild" would be a remedy for nothing. ⚠ And semantic
        // search switched off is ANOTHER choice, with another remedy: named as the missing model, it sent the user to
        // install one for an index that would still embed nothing (/index rebuild builds a keyword index whatever is
        // installed).
        var holeStatus = !_config.RagEnabled
            ? " (keyword search only: semantic search is off — ragEnabled)"
            : QueryEmbeddingModel is null && _passedOver is { Count: > 0 } passedOver
            ? $" (keyword search only: {string.Join(", ", passedOver)} {(passedOver.Count == 1 ? "searches" : "search")} code "
              + $"worse than keywords alone — {EmbeddingModels.Preferred} searches it better)"
            : QueryEmbeddingModel is null
            ? " (keyword search only: no embedding model is set or installed)"
            : unembedded > 0
            ? $" ({unembedded} of {total} chunks without embedding — semantic search misses them; run /index rebuild)"
            : string.Empty;
        var embStatus = _client.IsEmbeddingCircuitOpen ? " (embedding ⚠ circuit open, keyword fallback)" : string.Empty;
        // A model named in the settings is used as named — one an earlier first run wrote there too: said when it is a
        // family that searches code worse than keywords alone, so the choice is an informed one.
        var belowStatus = QueryEmbeddingModel is { } named && EmbeddingModels.SearchesCodeBelowKeywords(named)
            ? $" ({named} searches code worse than keywords alone — {EmbeddingModels.Preferred} searches it better)"
            : string.Empty;
        return $"RAG: ✅ {total} chunks from {files} files{holeStatus}{belowStatus}{embStatus}";
    }

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

    /// <summary>Whether <paramref name="file"/> is past the size the index takes — the full pass's rule, one reader.</summary>
    private static bool IsOversize(string file)
    {
        try { return new FileInfo(file).Length >= CodeChunker.MaxFileSizeBytes; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Takes a file out of the index — memory and database — O(1) per file.</summary>
    private async Task ForgetFileAsync(string file, RagDatabase db, CancellationToken ct)
    {
        await _chunkLock.WaitAsync(ct);
        try
        {
            _chunksByFile.Remove(file);
            ChunkCount = _chunksByFile.Values.Sum(l => l.Count);
            _contentVersion++;
        }
        finally { _chunkLock.Release(); }

        CaughtUp(file);

        try { await db.DeleteFileAsync(file, ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Swallow("ProjectIndexService.DeleteFile", ex); }
    }

    private async Task ReIndexFilesCoreAsync(string[] changedFiles, string rootDir, CancellationToken ct)
    {
        var db       = new RagDatabase(rootDir);
        // ⚠ The model the last pass chose, while the index has one: the vectors in memory and every query are that
        // model's. A file re-embedded with a model configured since was noise for every query — another model, the same
        // dimensions — until the next pass, which re-embeds everything with the new one. The field only for an index
        // that has no model yet; a re-index never lists the backend's models.
        var embModel = EmbeddingModels.ModelOfIdentity(_indexedEmbeddingModel) ?? EmbeddingModels.Configured(_config);
        // The vectors in memory came from the last pass's model: a model changed since cannot reuse them.
        var canReuse = string.Equals(EmbeddingModels.CodeIndexIdentity(embModel), _indexedEmbeddingModel,
                                     StringComparison.Ordinal);

        foreach (var file in changedFiles)
        {
            if (!File.Exists(file))
            {
                await ForgetFileAsync(file, db, ct);   // file deleted
                continue;
            }

            if (!CodeChunker.SupportedExtensions.Contains(
                    Path.GetExtension(file))) continue;

            // ⚠ Same size rule as the full pass, which leaves such a file out (and /index says so): without it a 2 MB
            // bundle saved once was chunked and embedded on the GPU — again at every save — and written to the index
            // /index still called "too large, not indexed", until the next pass dropped it again. What an earlier,
            // smaller version left in the index goes with it.
            if (IsOversize(file))
            {
                await ForgetFileAsync(file, db, ct);
                continue;
            }

            // ⚠ And excluded since it was indexed: its chunks would keep answering searches, the file the workspace
            // asked to keep out of reach.
            if (ContentExclusions.Of(file) is not null)
            {
                await ForgetFileAsync(file, db, ct);
                continue;
            }

            try
            {
                var content    = await Tools.TextFileEncoding.ReadTextAsync(file, ct);
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
                    if (!_config.RagEnabled || embModel is null || _client.IsEmbeddingCircuitOpen) continue;
                    // Yield the machine's graphics card to any in-flight interactive request — not a server elsewhere.
                    if (_client.SharesGpuWithChat) await GpuScheduler.WaitForChatIdleAsync(ct);
                    chunk.Embedding = await EmbeddingModels.EmbedCodeDocumentAsync(_client, embModel, chunk.Content, ct);
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
                CaughtUp(file);

                // Surgical SQLite write — only this file's rows are touched
                try { await db.SaveFileAsync(file, fileChunks, ct); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Diagnostics.Swallow("ProjectIndexService.SaveFile", ex); }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Diagnostics.Swallow("ProjectIndexService.ReIndexFile", ex); }
        }

        // The status is recounted from the index as it now is: holes made or filled by these files included.
        if (_passCompleted)
        {
            _updatedAt = DateTime.Now;
            Status     = await ReadyStatusAsync(ct);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

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
            foreach (var group in chunks.GroupBy(c => c.FilePath, PathComparer.Default))
                _chunksByFile[group.Key] = [.. group];
            ChunkCount = _chunksByFile.Values.Sum(l => l.Count);
            _contentVersion++;   // the search snapshot is stale from here
        }
        finally
        {
            _chunkLock.Release();
        }
    }

    /// <summary>Whether the search index leaves <paramref name="path"/> out: the project profile's exclusions, and the
    /// workspace's ignore files (<see cref="ContentExclusions"/>) — what they exclude is never chunked nor embedded.</summary>
    private bool IsExcluded(string path) =>
        IndexExclusions.IsExcluded(path, RootDir, _profileExcludes) || ContentExclusions.Of(path) is not null;

    /// <summary>
    /// Collects source files under <paramref name="rootDir"/>, skipping generated
    /// artifacts (bin, obj, .git, node_modules, .vs, .inferpal), whatever the project profile
    /// adds (<c>.inferpal/project.json</c>), and oversized files.
    /// </summary>
    /// <returns>The files, or <c>null</c> when the walk itself failed part-way — a partial list must
    /// not reach the pass's final replaceAll.</returns>
    private List<string>? EnumerateSourceFiles(string rootDir)
    {
        var result   = new List<string>();
        var tooBig   = 0;
        var tooBigAt = new List<string>();
        var excludedByIgnore = IgnoredSourceFiles.None;
        // ⚠ Once per pass, BEFORE the walk: `IgnoreInaccessible` skips an unlistable folder without
        // throwing, so the `catch` below never sees it and the `null` it returns — "partial list,
        // does not replace the index" — does not fire either. The pass is legitimate (nothing better
        // can be done), but it has to SAY so: this is the only trace that a whole folder is missing
        // from the index, and it survives on disk.
        SkippedFolder = WorkspaceScan.FirstWalkGap(rootDir, rootDir);
        try
        {
            // ONE walk filtered by extension: the per-extension loop walked the whole tree once for
            // every supported language. Counted: what the ignore files keep out of it is said by search_codebase.
            var listed = ContentExclusions.Counting(() => WorkspaceScan.EnumerateFiles(rootDir, "*").ToList(), out var ignored);
            ignored = ignored.Only(f => CodeChunker.SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
            excludedByIgnore = IgnoredSourceFiles.Of(ignored);
            foreach (var f in listed)
            {
                if (!CodeChunker.SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)
                    || IsExcluded(f))
                    continue;
                try
                {
                    if (new FileInfo(f).Length < CodeChunker.MaxFileSizeBytes) result.Add(f);
                    else if (++tooBig <= OversizeNamesCap) tooBigAt.Add(Path.GetRelativePath(rootDir, f));
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
        // ⚠ Published only on the path that actually replaces the index: the `null` returns above
        // mean "partial list, keep the previous index", so a count taken from them would describe a
        // pass whose result was thrown away.
        SkippedBySize = tooBig;
        OversizeFiles = tooBigAt;
        ExcludedByIgnoreFiles = excludedByIgnore;
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

    /// <summary>The service was disposed: no watcher, no pass.</summary>
    internal bool Disposed => _disposed;

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
        _dirWatcher?.Dispose();
        _rootWatcher?.Dispose();
        _watchedRoot = null;

        // Deliberately NOT disposing _chunkLock/_shadowLock: a background indexing pass may still
        // be awaiting them, and disposing a SemaphoreSlim under a waiter turns a clean cancellation
        // into an ObjectDisposedException in a detached task. They die with the process.
    }
}

/// <summary>The source files the workspace's ignore files keep out of the index, and the rules that do — as the coverage
/// note of <c>search_codebase</c> says them.</summary>
internal sealed record IgnoredSourceFiles(int Count, string Rules)
{
    /// <summary>None kept out.</summary>
    public static readonly IgnoredSourceFiles None = new(0, string.Empty);

    /// <summary>What a walk's tally counted.</summary>
    public static IgnoredSourceFiles Of(ContentExclusions.Tally tally) =>
        tally.Count == 0 ? None : new(tally.Count, tally.Rules());
}

/// <summary>The index as <see cref="ProjectIndexService.SnapshotAsync"/> reads it — what the settings card renders.</summary>
/// <param name="Done">Files the running pass has read; with <paramref name="Total"/>, its progress.</param>
/// <param name="Completed">The last full pass reached its end: only then are the counts the index's.</param>
/// <param name="Model">The model the index's vectors came from; <c>null</c> = keyword search only.</param>
/// <param name="Failure">Why the last pass stopped, when it failed (in the system's language: an exception message).</param>
/// <param name="Saved">The index shown is the one saved for the folder, read without a pass
/// (<see cref="ProjectIndexService.ShowSavedAsync"/>); <paramref name="UpdatedAt"/> is then when it was saved.</param>
/// <param name="Loading">The saved index is being read: what it holds is not known yet.</param>
/// <param name="PassedOver">With <paramref name="Model"/> <c>null</c>: the installed embedding models the automatic choice
/// passed over for searching code worse than keywords alone.</param>
internal sealed record IndexSnapshot(
    string RootDir, bool IsIndexing, int Done, int Total, bool Completed, int Files, int Chunks, int Unembedded,
    int Oversize, IReadOnlyList<string> OversizeFiles, WorkspaceScan.WalkGap? SkippedFolder, string? Model,
    bool EmbeddingDown, DateTime? UpdatedAt, string? Failure, bool Stopped, bool Saved = false, bool Loading = false,
    IReadOnlyList<string>? PassedOver = null);
