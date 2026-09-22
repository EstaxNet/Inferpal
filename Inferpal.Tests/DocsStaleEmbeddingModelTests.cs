using System.IO;
using System.Linq;
using Inferpal.Config;
using Inferpal.Services.Docs;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Vectors produced by ANOTHER embedding model are not reusable, and the documentation index must
/// not go on serving them.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ The rule is written on the twin, with its reason: <i>"Vectors from ANOTHER embedding model are
/// not reusable: other dimensions give a cosine of 0 everywhere, the same dimensions give noise —
/// under a ✅."</i> <c>ProjectIndexService</c> records the model beside the vectors and drops them
/// all when it changes. <c>DocsIndexService</c> reads <c>EmbeddingModel</c> in its embedding loop and
/// <b>nowhere else</b>: it never records it and never compares it, and <c>docs.db</c>'s <c>meta</c>
/// table holds only the schema version.
/// </para>
/// <para>
/// ⚠ It bites harder here than it would on the twin, for the reason this corpus always makes worse:
/// the code index re-runs its pass at <b>every startup</b> and would repair itself, while the
/// documentation index only ever <b>hydrates</b>. Nothing re-embeds a documentation site until the
/// user asks, so a stale vector is served for as long as the database lives — restarts included.
/// </para>
/// <para>
/// ⚠ And it is silent in the worst way: the chunks still <b>have</b> vectors, so the hole counter is
/// 0 and the status line stays a ✅ with its chunk count. With different dimensions the cosine is 0
/// everywhere, so the vector half returns nothing and the report calls itself "keyword" — the user
/// reads that their documentation is not indexed for meaning. With the same dimensions it is worse:
/// the numbers are noise, and nothing anywhere says so.
/// </para>
/// </remarks>
[Collection(DocsDatabaseCollection.Name)]
public sealed class DocsStaleEmbeddingModelTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"docsmodel-{Guid.NewGuid():N}");

    public DocsStaleEmbeddingModelTests() => DocsDatabase.OverrideDirForTests = _dir;

    public void Dispose()
    {
        DocsDatabase.OverrideDirForTests = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string Body(string marker) => string.Join(" ",
        Enumerable.Range(0, 200).Select(i => i % 20 == 0 ? marker : $"word{i % 11}"));

    /// <summary>Indexes one site with <paramref name="model"/> as the embedding model.</summary>
    private static async Task IndexWithAsync(string model)
    {
        var docs = new DocsIndexService(
            new FakeInferenceProvider { Embedding = [1f, 0f] },
            new InferpalConfig { RagEmbeddingModel = model })
        {
            CrawlForTests = (_, _) => Task.FromResult(new List<DocCrawler.Page>
            {
                new("https://docs.example.com/a", "Alpha", Body("alpha")),
            }),
        };
        await docs.AddOrReindexAsync(
            DocSite.Create("https://docs.example.com/", "Example"), progress: null, CancellationToken.None);

        // WITNESS: the pass really embedded something, otherwise every assertion below would be
        // satisfied by an index that never had a vector to lose.
        Assert.True(docs.ChunkCount > 0, "nothing was indexed.");
        Assert.Equal(0, docs.UnembeddedCount);
    }

    /// <summary>A fresh service over the same database, as the next session gets it.</summary>
    private static async Task<DocsIndexService> ReopenWithAsync(string model)
    {
        var docs = new DocsIndexService(
            new FakeInferenceProvider { Embedding = [1f, 0f] },
            new InferpalConfig { RagEmbeddingModel = model });
        await docs.LoadAsync(CancellationToken.None);
        return docs;
    }

    [Fact]
    public async Task AfterTheEmbeddingModelChanges_TheOldVectorsAreNotServed()
    {
        await IndexWithAsync("model-a");

        var docs = await ReopenWithAsync("model-b");

        // WITNESS: the corpus really came back from disk — otherwise this measures an empty index.
        Assert.True(docs.ChunkCount > 0, "nothing was hydrated: this test would measure nothing.");

        var hits = await docs.SearchAsync([1f, 0f], keywordFallback: null, 10, CancellationToken.None);

        Assert.Empty(hits);
        Assert.Equal(docs.ChunkCount, docs.UnembeddedCount);
    }

    [Fact]
    public async Task TheHoleIsAnnounced_SoTheRemedyIsNamed()
    {
        // Dropping the vectors in silence would trade a wrong answer for an unexplained one: the
        // hole note is what tells the user how many and what to run.
        await IndexWithAsync("model-a");

        var docs = await ReopenWithAsync("model-b");

        Assert.Contains("/docs reindex", docs.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WithTheSameModel_TheVectorsSurviveTheRestart()
    {
        // REFERENCE ARM: without it, "drop every vector on load" passes both tests above and turns
        // the documentation index into a keyword search for good.
        await IndexWithAsync("model-a");

        var docs = await ReopenWithAsync("model-a");

        Assert.Equal(0, docs.UnembeddedCount);

        var hits = await docs.SearchAsync([1f, 0f], keywordFallback: null, 10, CancellationToken.None);

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.True(h.IsCosine));
    }
}
