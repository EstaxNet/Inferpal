using System.IO;
using System.Linq;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.Docs;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>search_docs</c> says which half of the search produced each hit, reading it from the data.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ The twin index kept the defect the code index had already closed: the label and the whole score
/// column were decided on <c>results[0].Score is &gt; 0f and &lt; 1.001f</c>. ⭐ <i>A fix that closes
/// the instance one saw leaves the class alive</i> — the readers written for it
/// (<c>RagHit.IsCosine</c>, <c>RagResultPresentation</c>) were called "the single readers" while a
/// second index answered the same question its own way.
/// </para>
/// <para>
/// ⚠ It bites harder here than on the code index, and for a reason this repository already measured:
/// the lexical half is the <b>only</b> one that reaches chunks held <b>without a vector</b>, which is
/// the documented permanent state of a corpus whose embedding pass met an open circuit. So on the
/// corpus that most needs the distinction, every result that matters carries a score that is not a
/// similarity — printed next to real ones, or hidden while its neighbours show theirs.
/// </para>
/// </remarks>
[Collection(DocsDatabaseCollection.Name)]
public sealed class DocsSearchProvenanceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"docsprov-{Guid.NewGuid():N}");

    public DocsSearchProvenanceTests() => DocsDatabase.OverrideDirForTests = _dir;

    public void Dispose()
    {
        DocsDatabase.OverrideDirForTests = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Enough text for the chunker to produce several chunks per page.</summary>
    private static string Page(string marker) => string.Join(" ",
        Enumerable.Range(0, 4_000).Select(i => i % 40 == 0 ? marker : $"word{i % 17}"));

    private static DocSite Site => DocSite.Create("https://docs.example.com/", "Example");

    /// <summary>
    /// A corpus in the state the product documents as permanent: the first pages carry a vector, the
    /// rest were persisted without one because the embedding circuit opened mid-crawl.
    /// </summary>
    /// <remarks>
    /// ⚠ Both pages share the query's word, so the LEXICAL side ranks all of them while the vector
    /// side can only ever rank the embedded ones — a chunk with no vector scores a cosine of 0 and is
    /// dropped by the similarity threshold. That is what makes the ranking mixed, and it is the real
    /// shape rather than one arranged for the test.
    /// </remarks>
    private static async Task<DocsIndexService> IndexedAsync(int embedFirst)
    {
        var served = 0;
        var client = new FakeInferenceProvider
        {
            // The chunks' vectors, then the query's: same direction, so the embedded chunks come back
            // at cosine 1 and the un-embedded ones cannot come back through the vector side at all.
            OnEmbedding = _ => served++ < embedFirst ? [1f, 0f] : null,
        };

        var docs = new DocsIndexService(client, new InferpalConfig())
        {
            CrawlForTests = (_, _) => Task.FromResult(new List<DocCrawler.Page>
            {
                new("https://docs.example.com/a", "Retries",  Page("retries")),
                new("https://docs.example.com/b", "Timeouts", Page("retries")),
            }),
        };
        await docs.AddOrReindexAsync(Site, progress: null, CancellationToken.None);
        return docs;
    }

    private static JsonElement Raw(string json) => JsonDocument.Parse(json).RootElement;

    // ── The provenance itself ─────────────────────────────────────────────────

    [Fact]
    public async Task AFusedHit_IsACosineOnlyIfTheVectorSideRankedIt()
    {
        var docs = await IndexedAsync(embedFirst: 3);

        var hits = await docs.SearchAsync([1f, 0f], "retries", 10, CancellationToken.None);

        // WITNESS, both halves: the ranking really is mixed, so the assertion below discriminates.
        // With every hit on the same side this test would pass on a hard-coded boolean.
        Assert.Contains(hits, h => h.IsCosine);
        Assert.Contains(hits, h => !h.IsCosine);

        // A hit the vector side ranked carries a real similarity.
        Assert.All(hits.Where(h => h.IsCosine),
                   h => Assert.True(h.Score is > 0f and < 1.001f,
                                    $"a cosine hit scored {h.Score}, which is not a similarity."));

        // ⚠ And one it did not carries `0f` — which is exactly what a similarity of zero looks like.
        // THAT is why the boolean exists: the number cannot be asked.
        Assert.All(hits.Where(h => !h.IsCosine),
                   h => Assert.Equal(0f, h.Score));
    }

    [Fact]
    public async Task WithNoVectorSideAtAll_NoHitClaimsToBeACosine()
    {
        // The purely lexical fallback, reached here with an embedding that is ORTHOGONAL to every
        // chunk: the similarity threshold drops the whole vector side. A BM25 score arrives in the
        // same `float` a cosine would, which is the second half of the defect.
        var docs = await IndexedAsync(embedFirst: int.MaxValue);

        var hits = await docs.SearchAsync([0f, 1f], "retries", 10, CancellationToken.None);

        Assert.NotEmpty(hits);                       // witness: the lexical half really answered
        Assert.All(hits, h => Assert.False(h.IsCosine));
    }

    // ── What the model reads ──────────────────────────────────────────────────

    private static SearchDocsTool Tool(DocsIndexService docs, float[]? queryVector) =>
        new(docs, new FakeInferenceProvider { Embedding = queryVector }, new InferpalConfig());

    [Fact]
    public async Task AMixedRanking_IsAnnouncedHybrid_AndOnlyTheCosinesShowAScore()
    {
        var docs = await IndexedAsync(embedFirst: 3);

        var report = await Tool(docs, [1f, 0f])
            .ExecuteAsync(Raw("""{"query":"retries","top_k":10}"""), CancellationToken.None);

        // WITNESS: a report was rendered, not an early "nothing found" return.
        Assert.Contains("## Documentation search", report, StringComparison.Ordinal);

        // Read from EVERY hit: with the label taken from the first one, a lexical hit in front made
        // the whole report announce `keyword` — the model then reads that the semantic index did not
        // serve, on a search where it ranked half the results.
        Assert.Contains("(hybrid,", report, StringComparison.Ordinal);

        // And the column is per hit: fewer scores than results, because the lexical-only hits show
        // none. A number the reader cannot compare is worse than no number.
        var rows   = report.Split("### [", StringSplitOptions.RemoveEmptyEntries).Length - 1;
        var scored = report.Split(" · score ", StringSplitOptions.None).Length - 1;
        Assert.True(rows > 0, "no result row was rendered: this assertion would measure nothing.");
        Assert.InRange(scored, 1, rows - 1);
    }

    [Fact]
    public async Task AKeywordOnlyRanking_ShowsNoScoreAtAll()
    {
        // REFERENCE ARM on the other side: a BM25 score under 1.001 used to print as `score 0.840`
        // beside real similarities. Nothing here may claim to be one.
        var docs = await IndexedAsync(embedFirst: int.MaxValue);

        var report = await Tool(docs, [0f, 1f])
            .ExecuteAsync(Raw("""{"query":"retries","top_k":10}"""), CancellationToken.None);

        Assert.Contains("## Documentation search", report, StringComparison.Ordinal);
        Assert.Contains("(keyword,", report, StringComparison.Ordinal);
        Assert.DoesNotContain(" · score ", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAllCosineRanking_IsStillAnnouncedSemantic()
    {
        // REFERENCE ARM: without it, a build that answered `hybrid` (or showed no score) always
        // would pass everything above while measuring nothing.
        var docs = await IndexedAsync(embedFirst: int.MaxValue);

        var report = await Tool(docs, [1f, 0f])
            .ExecuteAsync(Raw("""{"query":"retries","top_k":10}"""), CancellationToken.None);

        Assert.Contains("(semantic,", report, StringComparison.Ordinal);
        Assert.Contains(" · score ", report, StringComparison.Ordinal);
    }
}
