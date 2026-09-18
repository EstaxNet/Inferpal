using System.IO;
using System.Linq;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.Commands;
using Inferpal.Services.Docs;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A documentation chunk saved <b>without a vector</b> is invisible to the semantic half of
/// <c>search_docs</c> — for good, and nothing said how many there were.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ The embedding loop stops as soon as the circuit opens (backend down, model missing, VRAM), and
/// a chunk can also come back with no vector on its own. Those chunks are persisted as they are, and
/// <b>nothing ever recomputes them</b>: only an explicit <c>/docs reindex</c> does. The pass said it
/// once, in a transient status line; the next session hydrates from <c>docs.db</c> and reports
/// <c>Docs: N chunks from M source(s)</c>, while <c>/docs list</c> shows a source as
/// <c>(50 pages, 400 chunks)</c> — a corpus that reads as fully indexed with three quarters of it
/// beyond the reach of semantic search.
/// </para>
/// <para>
/// ⚠ <b>The twin index already learned this.</b> <c>ProjectIndexService</c> counts its own holes and
/// names the remedy in the status that <c>/index</c> and <c>search_codebase</c> read:
/// <i>"(N of M chunks without embedding — semantic search misses them; run /index rebuild)"</i>. The
/// difference that makes @Docs worse: an indexing pass runs at every boot for the code index, so its
/// count is recomputed; the docs index only ever hydrates.
/// </para>
/// </remarks>
[Collection(DocsDatabaseCollection.Name)]
public sealed class DocsEmbeddingHoleTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"docshole-{Guid.NewGuid():N}");

    public DocsEmbeddingHoleTests() => DocsDatabase.OverrideDirForTests = _dir;

    public void Dispose()
    {
        DocsDatabase.OverrideDirForTests = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Enough text for the chunker to produce several chunks (~500 tokens each).</summary>
    private static string Page(string marker) => string.Join(" ",
        Enumerable.Range(0, 4_000).Select(i => i % 40 == 0 ? marker : $"word{i % 17}"));

    private static DocSite Site => DocSite.Create("https://docs.example.com/", "Example");

    /// <param name="embedFirst">How many chunks get a vector; the rest come back without one.</param>
    private static async Task<DocsIndexService> IndexedAsync(int embedFirst)
    {
        var served = 0;
        var client = new FakeInferenceProvider
        {
            OnEmbedding = _ => served++ < embedFirst ? [0.1f, 0.2f] : null,
        };
        var docs = new DocsIndexService(client, new InferpalConfig())
        {
            CrawlForTests = (_, _) => Task.FromResult(new List<DocCrawler.Page>
            {
                new("https://docs.example.com/a", "Retries", Page("retries")),
                new("https://docs.example.com/b", "Timeouts", Page("timeouts")),
            }),
        };
        await docs.AddOrReindexAsync(Site, progress: null, CancellationToken.None);
        return docs;
    }

    /// <summary>A second service over the same <c>docs.db</c>: what the next session sees.</summary>
    private static async Task<DocsIndexService> RehydratedAsync()
    {
        var docs = new DocsIndexService(new FakeInferenceProvider(), new InferpalConfig());
        await docs.LoadAsync(CancellationToken.None);
        return docs;
    }

    // ── The status the next session reads ─────────────────────────────────────

    [Fact]
    public async Task AfterARestart_TheStatus_CountsTheChunksWithoutAVector()
    {
        var indexed = await IndexedAsync(embedFirst: 2);
        Assert.True(indexed.ChunkCount > 2, "the fixture produced too few chunks to have a hole at all.");

        var docs = await RehydratedAsync();

        Assert.Equal(indexed.ChunkCount, docs.ChunkCount);          // witness: it really hydrated
        Assert.Contains("without embedding", docs.Status, StringComparison.Ordinal);
        // The remedy, named: nothing else recomputes those vectors.
        Assert.Contains("/docs reindex", docs.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AfterARestart_WithEveryChunkEmbedded_TheStatusSaysNothingAboutHoles()
    {
        // NEGATIVE WITNESS: without it, a build that always appended the note would pass everything
        // here while measuring nothing.
        await IndexedAsync(embedFirst: int.MaxValue);

        var docs = await RehydratedAsync();

        Assert.True(docs.ChunkCount > 0, "nothing was indexed: this test would measure nothing.");
        Assert.DoesNotContain("without embedding", docs.Status, StringComparison.Ordinal);
    }

    // ── /docs list ────────────────────────────────────────────────────────────

    private static async Task<string> DocsListAsync(DocsIndexService docs)
    {
        var config = new InferpalConfig { DocSitesJson = DocSite.Serialize([Site]) };
        return await DocsCommandHandler.HandleAsync(
            config, docs, ["/docs"], new Progress<string>(_ => { }), CancellationToken.None);
    }

    [Fact]
    public async Task DocsList_ShowsTheHolePerSource_WithItsRemedy()
    {
        await IndexedAsync(embedFirst: 2);
        var docs = await RehydratedAsync();

        var listing = await DocsListAsync(docs);

        Assert.Contains("Example", listing, StringComparison.Ordinal);   // witness: the source is listed
        Assert.Contains("without embedding", listing, StringComparison.Ordinal);
        Assert.Contains("/docs reindex", listing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsList_WithEveryChunkEmbedded_ShowsNoHole()
    {
        await IndexedAsync(embedFirst: int.MaxValue);
        var docs = await RehydratedAsync();

        var listing = await DocsListAsync(docs);

        Assert.Contains("Example", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("without embedding", listing, StringComparison.Ordinal);
    }

    // ── search_docs ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchDocs_CarriesTheIndexStatus_LikeSearchCodebaseDoes()
    {
        // ⚠ The model is the one acting on "it is not in the documentation". `search_codebase` ends
        // every answer with its index status for exactly this reason; `search_docs` ended with the
        // last excerpt.
        await IndexedAsync(embedFirst: 2);
        var docs = await RehydratedAsync();

        var tool = new SearchDocsTool(docs, new FakeInferenceProvider { Embedding = [0.1f, 0.2f] }, new InferpalConfig());
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { query = "retries" }));

        var answer = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);

        Assert.Contains("Documentation search", answer, StringComparison.Ordinal);   // witness: it ran
        Assert.Contains("without embedding", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchDocs_WithAWholeCorpus_SaysNothingAboutHoles()
    {
        await IndexedAsync(embedFirst: int.MaxValue);
        var docs = await RehydratedAsync();

        var tool = new SearchDocsTool(docs, new FakeInferenceProvider { Embedding = [0.1f, 0.2f] }, new InferpalConfig());
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { query = "retries" }));

        var answer = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);

        Assert.Contains("Documentation search", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("without embedding", answer, StringComparison.Ordinal);
    }
}
