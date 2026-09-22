using Inferpal.Config;

using Inferpal.Services.Rag;

namespace Inferpal.Services.Docs;

/// <summary>
/// In-memory index of external documentation, backed by the global <see cref="DocsDatabase"/>.
/// Crawls documentation sites, embeds their text via Ollama, and serves semantic retrieval to
/// the <c>search_docs</c> tool. Documentation is shared across all solutions.
/// </summary>
/// <remarks>
/// Lifecycle: call <see cref="LoadAsync"/> once at startup to hydrate from disk, then add
/// sources with <see cref="AddOrReindexAsync"/> (triggered by <c>/docs add</c>). Search via
/// <see cref="SearchAsync"/>. Mirrors the embedding throttle and circuit-breaker handling of
/// <see cref="Rag.ProjectIndexService"/> so docs indexing never floods the shared Ollama backend.
/// </remarks>
internal sealed class DocsIndexService
{
    private readonly IInferenceProvider _client;
    private readonly InferpalConfig _config;

    private readonly SemaphoreSlim _chunkLock = new(1, 1);
    private readonly SemaphoreSlim _indexLock = new(1, 1);

    // The source the running pass indexes, and how to stop it. Guarded by _passGate.
    private readonly object _passGate = new();
    private (string SiteId, CancellationTokenSource Cts)? _pass;

    private List<DocChunk> _chunks = [];
    private List<(DocSite Site, int PageCount, int ChunkCount)> _sites = [];

    public DocsIndexService(IInferenceProvider client, InferpalConfig config)
    {
        _client = client;
        _config = config;
    }

    /// <summary>Tests replace the network crawl (and its SSRF guard) with pages they control.</summary>
    internal Func<string, CancellationToken, Task<List<DocCrawler.Page>>>? CrawlForTests { get; init; }

    // ── Public state ──────────────────────────────────────────────────────────

    /// <summary>Human-readable status of the last/ongoing indexing pass.</summary>
    public string Status     { get; private set; } = string.Empty;

    /// <summary><c>true</c> while a crawl/embed pass is running.</summary>
    public bool   IsIndexing { get; private set; }

    /// <summary>Number of documentation chunks currently held in memory.</summary>
    public int    ChunkCount { get; private set; }

    /// <summary>
    /// Chunks held with <b>no vector</b>: invisible to the semantic half of the search, and nothing
    /// recomputes them.
    /// </summary>
    /// <remarks>
    /// The embedding loop stops as soon as the circuit opens, and a chunk can come back without a
    /// vector on its own; both are persisted as they are. Unlike the code index — whose pass runs at
    /// every boot and recounts — this one only ever hydrates, so a hole from one bad afternoon was
    /// permanent and invisible: the status read "Docs: 400 chunks from 1 source(s)" and the listing
    /// "(50 pages, 400 chunks)". The only remedy is an explicit /docs reindex, so it is named.
    /// </remarks>
    public int    UnembeddedCount { get; private set; }

    /// <summary>The same count per source id, for the <c>/docs</c> listing.</summary>
    public IReadOnlyDictionary<string, int> UnembeddedBySite { get; private set; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The sentence a hole gets, or an empty string. One reader for the status, the listing and
    /// <c>search_docs</c> — three sites that would otherwise each phrase the same gap differently.
    /// </summary>
    internal static string HoleNote(int unembedded, int total, string? siteId = null) =>
        unembedded <= 0
            ? string.Empty
            : $" ({unembedded} of {total} chunks without embedding — semantic search misses them; "
            + $"run /docs reindex{(siteId is { Length: > 0 } ? " " + siteId : string.Empty)})";

    /// <summary>Snapshot of the configured documentation sources with their crawl stats.</summary>
    /// <remarks>
    /// Async because the lock it takes is also taken by async code. The synchronous
    /// <c>_chunkLock.Wait()</c> this replaces held today only by accident: no path currently keeps
    /// <c>_chunkLock</c> across an <c>await</c> (the network work sits behind a different
    /// semaphore, <c>_indexLock</c>), so nothing deadlocked — but that is an invariant nobody
    /// stated and no test defends, and the day someone adds an <c>await</c> inside one of those
    /// sections, <c>/docs</c> freezes the calling thread with no error.
    /// </remarks>
    public async Task<IReadOnlyList<(DocSite Site, int PageCount, int ChunkCount)>> SitesAsync(
        CancellationToken ct = default)
    {
        await _chunkLock.WaitAsync(ct);
        try { return _sites.ToList(); }
        finally { _chunkLock.Release(); }
    }

    private string EmbeddingModel =>
        string.IsNullOrEmpty(_config.RagEmbeddingModel) ? "nomic-embed-text" : _config.RagEmbeddingModel;

    // ── Load ─────────────────────────────────────────────────────────────────

    /// <summary>Hydrates the in-memory index from <c>docs.db</c>. Safe to call once at startup.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            var db     = new DocsDatabase();
            var sites  = await db.LoadSitesAsync(ct);
            var chunks = await db.LoadAllChunksAsync(ct);

            await PublishAsync(sites, chunks, ct);

            Status = chunks.Count > 0
                ? $"Docs: {chunks.Count} chunks from {sites.Count} source(s)"
                  + HoleNote(UnembeddedCount, chunks.Count)
                : "Docs: no documentation indexed";
        }
        catch (Exception ex)
        {
            Status = $"Docs: load error — {ex.Message}";
        }
    }

    // ── Add / re-index ─────────────────────────────────────────────────────────

    /// <summary>
    /// Crawls <paramref name="site"/>, chunks and embeds every page, then persists the result and
    /// refreshes the in-memory index. Existing chunks for the same source are replaced.
    /// </summary>

    /// <summary>
    /// What a <c>/docs index</c> actually got, said to the user.
    /// </summary>
    /// <remarks>
    /// ⚠ An address refused by the SSRF guard produced "no readable pages found" — the message of
    /// an empty site. A local documentation server (<c>localhost:8000</c>, an intranet host) is the
    /// most likely case on a 100 % local product, so the user went off to check a site that works
    /// perfectly in their browser. The cause is named.
    /// ⚠ And the <see cref="DocCrawler.MaxPages"/> page cap is stated when it is reached:
    /// otherwise <c>@Docs</c> answers "not in the documentation" about half a site the user
    /// believes is indexed in full.
    /// </remarks>
    internal static string DescribeCrawlOutcome(string startUrl, int pageCount, bool refused)
    {
        if (refused)
            return $"Docs: {startUrl} is a private or loopback address — refused on purpose "
                 + "(the same guard that protects fetch_url). Nothing was indexed.";
        if (pageCount == 0)
            return $"Docs: no readable pages found at {startUrl}.";
        return pageCount >= DocCrawler.MaxPages
            ? $"Docs: {pageCount} pages (crawl limit of {DocCrawler.MaxPages} reached — the site may have more)"
            : $"Docs: {pageCount} pages";
    }

    /// <param name="stillWanted">Asked again once it is this source's turn: a source removed while it waited is
    /// not written back, since a removal stops only the pass that is already running.</param>
    public async Task AddOrReindexAsync(
        DocSite site, IProgress<string>? progress, CancellationToken ct, Func<DocSite, bool>? stillWanted = null)
    {
        // One pass at a time: a source asked for meanwhile waits its turn. Dropped, it stayed at 0 pages after
        // /docs add had announced it and saved it to the settings.
        if (!await _indexLock.WaitAsync(0, ct))
        {
            progress?.Report($"Docs: {site.Title} is queued behind the indexing pass already running.");
            try
            {
                await _indexLock.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                progress?.Report("Docs: indexing cancelled.");
                return;
            }
        }
        if (stillWanted is not null && !stillWanted(site))
        {
            _indexLock.Release();
            return;
        }

        IsIndexing = true;
        using var passCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_passGate) _pass = (site.Id, passCts);
        ct = passCts.Token;
        try
        {
            // ── Crawl ──────────────────────────────────────────────────────────
            progress?.Report($"Docs: crawling {site.Title}…");
            var crawler = new DocCrawler();
            var crawlProgress = new Progress<(int fetched, int total)>(p =>
                Status = $"Docs: crawling {site.Title} — {p.fetched}/{Math.Max(p.fetched, p.total)} pages");

            var refused = CrawlForTests is null && Tools.FetchUrlTool.IsPrivateOrLoopback(site.StartUrl);
            var pages   = refused ? []
                        : CrawlForTests is { } crawl ? await crawl(site.StartUrl, ct)
                        : await crawler.CrawlAsync(site.StartUrl, crawlProgress, ct);
            if (pages.Count == 0)
            {
                Status = DescribeCrawlOutcome(site.StartUrl, 0, refused);
                progress?.Report(Status);
                return;
            }

            // ── Chunk ──────────────────────────────────────────────────────────
            var chunks = new List<DocChunk>();
            foreach (var page in pages)
                chunks.AddRange(DocChunker.Chunk(site.Id, page.Url, page.Title, page.Text));

            progress?.Report($"Docs: {site.Title} — {pages.Count} pages, {chunks.Count} chunks; embedding…");

            // ── Embed (throttled, circuit-breaker aware) ─────────────────────────
            var embModel = EmbeddingModel;
            for (int i = 0; i < chunks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                if (_client.IsEmbeddingCircuitOpen) break; // keyword fallback still works

                // Yield the shared Ollama GPU to any in-flight chat/agent request.
                await GpuScheduler.WaitForChatIdleAsync(ct);
                var emb = await _client.GetEmbeddingAsync(chunks[i].Content, embModel, ct);
                chunks[i].Embedding = emb;

                if (i % 5 == 4)
                {
                    Status = $"Docs: embedding {site.Title} — {i + 1}/{chunks.Count}";
                    await Task.Delay(80, ct);
                }
            }

            // ── Persist + refresh memory ─────────────────────────────────────────
            var db = new DocsDatabase();
            await db.SaveSiteAsync(site, pages.Count, chunks, ct);
            await ReloadFromDbAsync(db, ct);

            // The circuit note says WHY; the hole note says HOW MUCH and what to do about it — and it
            // is the one that survives into the next session.
            var embNote = (_client.IsEmbeddingCircuitOpen ? " (⚠ embedding circuit open, keyword fallback)" : string.Empty)
                        + HoleNote(UnembeddedCount, ChunkCount);
            var crawlNote = pages.Count >= DocCrawler.MaxPages
                ? $" (crawl limit of {DocCrawler.MaxPages} pages reached — the site may have more)"
                : string.Empty;
            Status = $"Docs: ✅ {site.Title} — {pages.Count} pages, {chunks.Count} chunks{crawlNote}{embNote}";
            progress?.Report(Status);
        }
        catch (OperationCanceledException)
        {
            Status = "Docs: indexing cancelled.";
            progress?.Report(Status);
        }
        catch (Exception ex)
        {
            Status = $"Docs: error — {ex.Message}";
            progress?.Report(Status);
        }
        finally
        {
            lock (_passGate) _pass = null;
            IsIndexing = false;
            _indexLock.Release();
        }
    }

    /// <summary>Removes a documentation source and all of its chunks.</summary>
    public async Task RemoveAsync(string docId, CancellationToken ct)
    {
        // A pass still indexing this source writes it only when it ends — after this removal — and the
        // source would come back where search serves it and no command can remove it. Stop that pass and
        // let it end before deleting.
        CancellationTokenSource? toStop = null;
        lock (_passGate)
            if (_pass is { } running && string.Equals(running.SiteId, docId, StringComparison.OrdinalIgnoreCase))
                toStop = running.Cts;

        var stopped = toStop is not null;
        if (stopped)
        {
            try { toStop!.Cancel(); } catch (ObjectDisposedException) { /* the pass already ended */ }
            await _indexLock.WaitAsync(ct);
        }
        try
        {
            var db = new DocsDatabase();
            await db.DeleteSiteAsync(docId, ct);
            await ReloadFromDbAsync(db, ct);
            Status = $"Docs: {ChunkCount} chunks from {_sites.Count} source(s)"
                   + HoleNote(UnembeddedCount, ChunkCount);
        }
        finally
        {
            if (stopped) _indexLock.Release();
        }
    }

    /// <summary>
    /// Indexes <paramref name="sites"/> one after the other, skipping any source
    /// <paramref name="stillWanted"/> no longer accepts when its turn comes.
    /// </summary>
    /// <remarks>
    /// The list is taken when the command runs, and each pass takes minutes: a source removed before the
    /// loop reaches it would otherwise be indexed and written back, since removal only stops the pass
    /// that is already running. The check is made once the source holds the indexing slot.
    /// </remarks>
    public async Task ReindexAsync(IReadOnlyList<DocSite> sites, Func<DocSite, bool> stillWanted, IProgress<string>? progress)
    {
        foreach (var site in sites)
            await AddOrReindexAsync(site, progress, CancellationToken.None, stillWanted);
    }

    private async Task ReloadFromDbAsync(DocsDatabase db, CancellationToken ct) =>
        await PublishAsync(await db.LoadSitesAsync(ct), await db.LoadAllChunksAsync(ct), ct);

    /// <summary>
    /// The only place the in-memory index is replaced — and therefore the only place the hole is
    /// counted. Assigning <c>_chunks</c> and <c>ChunkCount</c> by hand elsewhere means every new
    /// site has to remember the count too.
    /// </summary>
    private async Task PublishAsync(
        List<(DocSite Site, int PageCount, int ChunkCount)> sites, List<DocChunk> chunks, CancellationToken ct)
    {
        await _chunkLock.WaitAsync(ct);
        try
        {
            _sites          = sites;
            _chunks         = chunks;
            ChunkCount      = chunks.Count;
            UnembeddedCount = chunks.Count(c => c.Embedding is not { Length: > 0 });
            UnembeddedBySite = chunks
                .Where(c => c.Embedding is not { Length: > 0 })
                .GroupBy(c => c.DocId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        }
        finally { _chunkLock.Release(); }
    }

    // ── Search ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the most relevant documentation chunks for the query. Uses cosine similarity when
    /// <paramref name="queryEmbedding"/> is provided, otherwise falls back to keyword matching.
    /// </summary>
    public async Task<List<DocHit>> SearchAsync(
        float[]? queryEmbedding, string? keywordFallback, int topK, CancellationToken ct)
    {
        List<DocChunk> all;
        await _chunkLock.WaitAsync(ct);
        try
        {
            if (_chunks.Count == 0) return [];
            all = _chunks.ToList(); // snapshot, then score outside the lock
        }
        finally { _chunkLock.Release(); }

        var pool = Math.Max(topK * 5, 50);

        List<(int Idx, float Cos)> vector = [];
        if (queryEmbedding is { Length: > 0 })
        {
            vector = VectorMath.RankBySimilarity(
                all, c => c.Embedding, queryEmbedding, _config.RagSimilarityThreshold, pool);
        }

        // ⚠ The lexical side is BM25 over the query's words, like the code index. The old keyword
        // fallback searched the WHOLE query as one substring, and search_docs passes the model's
        // sentence — "how to configure retries" never matched. It is also what reaches the chunks left
        // without a vector (circuit opened mid-crawl), which the vector side can never return.
        List<(int Idx, double Score)> lexical = [];
        if (!string.IsNullOrWhiteSpace(keywordFallback) && CodeTokenizer.Tokenize(keywordFallback) is { Count: > 0 } terms)
        {
            // ⚠ The tokens come from the CHUNK, not from its body: they carry the page title, the
            // heading and the URL, which is where documentation actually names its topic. They are
            // also cached per chunk — tokenizing the whole corpus on every query is the cost the
            // code index already removed.
            lexical = new Bm25Index(all.Select(c => c.Bm25Tokens).ToList()).Rank(terms, pool);
        }

        // ⚠ IsCosine says where THIS result came from, and nothing else can: a lexical-only hit
        // scores 0f, exactly like a similarity of zero. It matters more here than on the code index,
        // because the lexical side is the ONLY half that reaches the chunks held without a vector.
        if (vector.Count > 0 && lexical.Count > 0)
        {
            var cosByIdx = vector.ToDictionary(x => x.Idx, x => x.Cos);
            return ReciprocalRankFusion.Fuse(new[]
                {
                    vector.Select(x => x.Idx).ToList(),
                    lexical.Select(x => x.Idx).ToList(),
                })
                .Take(topK)
                .Select(i =>
                {
                    var (score, isCosine) = HybridProvenance.OfFused(i, cosByIdx);
                    return new DocHit(all[i], score, isCosine);
                })
                .ToList();
        }
        if (vector.Count > 0)
            return vector.Take(topK).Select(x => new DocHit(all[x.Idx], x.Cos, IsCosine: true)).ToList();
        // A BM25 score is unbounded and is NOT a similarity — saying so is the whole point.
        return lexical.Take(topK)
                      .Select(x => new DocHit(all[x.Idx], (float)x.Score, IsCosine: false))
                      .ToList();
    }
}
