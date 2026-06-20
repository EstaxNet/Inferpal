using Inferpal.Config;

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

    private List<DocChunk> _chunks = [];
    private List<(DocSite Site, int PageCount, int ChunkCount)> _sites = [];

    public DocsIndexService(IInferenceProvider client, InferpalConfig config)
    {
        _client = client;
        _config = config;
    }

    // ── Public state ──────────────────────────────────────────────────────────

    /// <summary>Human-readable status of the last/ongoing indexing pass.</summary>
    public string Status     { get; private set; } = string.Empty;

    /// <summary><c>true</c> while a crawl/embed pass is running.</summary>
    public bool   IsIndexing { get; private set; }

    /// <summary>Number of documentation chunks currently held in memory.</summary>
    public int    ChunkCount { get; private set; }

    /// <summary>Snapshot of the configured documentation sources with their crawl stats.</summary>
    public IReadOnlyList<(DocSite Site, int PageCount, int ChunkCount)> Sites
    {
        get { _chunkLock.Wait(); try { return _sites.ToList(); } finally { _chunkLock.Release(); } }
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

            await _chunkLock.WaitAsync(ct);
            try
            {
                _sites     = sites;
                _chunks    = chunks;
                ChunkCount = chunks.Count;
            }
            finally { _chunkLock.Release(); }

            Status = chunks.Count > 0
                ? $"Docs: {chunks.Count} chunks from {sites.Count} source(s)"
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
    public async Task AddOrReindexAsync(DocSite site, IProgress<string>? progress, CancellationToken ct)
    {
        if (!await _indexLock.WaitAsync(0, ct))
        {
            progress?.Report("Docs: another indexing pass is already running — try again shortly.");
            return;
        }

        IsIndexing = true;
        try
        {
            // ── Crawl ──────────────────────────────────────────────────────────
            progress?.Report($"Docs: crawling {site.Title}…");
            var crawler = new DocCrawler();
            var crawlProgress = new Progress<(int fetched, int total)>(p =>
                Status = $"Docs: crawling {site.Title} — {p.fetched}/{Math.Max(p.fetched, p.total)} pages");

            var pages = await crawler.CrawlAsync(site.StartUrl, crawlProgress, ct);
            if (pages.Count == 0)
            {
                progress?.Report($"Docs: no readable pages found at {site.StartUrl}.");
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

            var embNote = _client.IsEmbeddingCircuitOpen ? " (⚠ embedding circuit open, keyword fallback)" : string.Empty;
            Status = $"Docs: ✅ {site.Title} — {pages.Count} pages, {chunks.Count} chunks{embNote}";
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
            IsIndexing = false;
            _indexLock.Release();
        }
    }

    /// <summary>Removes a documentation source and all of its chunks.</summary>
    public async Task RemoveAsync(string docId, CancellationToken ct)
    {
        var db = new DocsDatabase();
        await db.DeleteSiteAsync(docId, ct);
        await ReloadFromDbAsync(db, ct);
        Status = $"Docs: {ChunkCount} chunks from {_sites.Count} source(s)";
    }

    private async Task ReloadFromDbAsync(DocsDatabase db, CancellationToken ct)
    {
        var sites  = await db.LoadSitesAsync(ct);
        var chunks = await db.LoadAllChunksAsync(ct);
        await _chunkLock.WaitAsync(ct);
        try
        {
            _sites     = sites;
            _chunks    = chunks;
            ChunkCount = chunks.Count;
        }
        finally { _chunkLock.Release(); }
    }

    // ── Search ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the most relevant documentation chunks for the query. Uses cosine similarity when
    /// <paramref name="queryEmbedding"/> is provided, otherwise falls back to keyword matching.
    /// </summary>
    public async Task<List<(DocChunk Chunk, float Score)>> SearchAsync(
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

        if (queryEmbedding is { Length: > 0 })
        {
            var ranked = all
                .Where(c => c.Embedding is { Length: > 0 })
                .Select(c => (c, CosineSimilarity(queryEmbedding, c.Embedding!)))
                .Where(x => x.Item2 >= _config.RagSimilarityThreshold)
                .OrderByDescending(x => x.Item2)
                .Take(topK)
                .ToList();
            if (ranked.Count > 0) return ranked;
        }

        if (!string.IsNullOrWhiteSpace(keywordFallback))
        {
            var kw = keywordFallback.ToLowerInvariant();
            return all
                .Select(c => (c, (float)CountMatches(c.Content, kw)))
                .Where(x => x.Item2 > 0)
                .OrderByDescending(x => x.Item2)
                .Take(topK)
                .ToList();
        }

        return [];
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
        float dot = 0f, normA = 0f, normB = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            dot   += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return (normA > 0f && normB > 0f)
            ? dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB))
            : 0f;
    }

    private static int CountMatches(string text, string keyword)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(keyword, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            idx += keyword.Length;
        }
        return count;
    }
}
