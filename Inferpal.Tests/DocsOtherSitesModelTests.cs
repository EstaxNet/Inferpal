using System.IO;
using System.Linq;
using Inferpal.Config;
using Inferpal.Services.Docs;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ The documentation corpus records ONE embedding model for all its sites. Re-indexing one site with a new model
/// recorded the new model, and the reload that follows compared the OTHER sites' old vectors to that record — they came
/// back as valid, the hole counter at 0, and their next pass reused them for good: a cosine of 0 or noise, under a ✅.
/// </summary>
[Collection(DocsDatabaseCollection.Name)]
public sealed class DocsOtherSitesModelTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"docsothers-{Guid.NewGuid():N}");

    public DocsOtherSitesModelTests() => DocsDatabase.OverrideDirForTests = _dir;

    public void Dispose()
    {
        DocsDatabase.OverrideDirForTests = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string Body(string marker) => string.Join(" ",
        Enumerable.Range(0, 200).Select(i => i % 20 == 0 ? marker : $"word{i % 11}"));

    private static DocsIndexService Service(string model, string page, string marker) => new(
        new FakeInferenceProvider { Embedding = [1f, 0f] },
        new InferpalConfig { RagEmbeddingModel = model })
    {
        CrawlForTests = (_, _) => Task.FromResult(new List<DocCrawler.Page> { new(page, marker, Body(marker)) }),
    };

    [Fact]
    public async Task ReindexingOneSite_WithANewModel_DoesNotRevalidateTheOthersOldVectors()
    {
        var alpha = DocSite.Create("https://alpha.example.com/", "Alpha");
        var beta  = DocSite.Create("https://beta.example.com/", "Beta");

        // Both sites embedded with model-a.
        var onlyAlpha = Service("model-a", "https://alpha.example.com/a", "alpha");
        await onlyAlpha.AddOrReindexAsync(alpha, null, CancellationToken.None);
        var first = Service("model-a", "https://beta.example.com/b", "beta");
        await first.AddOrReindexAsync(beta, null, CancellationToken.None);
        Assert.Equal(0, first.UnembeddedCount);                                   // witness: everything had a vector
        var betaChunks = first.ChunkCount - onlyAlpha.ChunkCount;

        // The model becomes model-b, and only Alpha is re-indexed.
        var docs = Service("model-b", "https://alpha.example.com/a", "alpha");
        await docs.LoadAsync(CancellationToken.None);
        await docs.AddOrReindexAsync(alpha, null, CancellationToken.None);

        // Beta's vectors came from model-a: they are a hole now, not valid vectors.
        Assert.True(betaChunks > 0, "Beta has no chunk: the rule reads nothing");   // WITNESS
        Assert.True(docs.UnembeddedCount >= betaChunks,
            $"{docs.UnembeddedCount} chunk(s) without a vector, Beta's model-a vectors were revalidated");
    }

    [Fact]
    public async Task ReindexingOneSite_WithTheSameModel_KeepsTheOthersVectors()
    {
        // Reference arm: no model change, nothing of the other site is dropped.
        var alpha = DocSite.Create("https://alpha.example.com/", "Alpha");
        var beta  = DocSite.Create("https://beta.example.com/", "Beta");
        await Service("model-a", "https://alpha.example.com/a", "alpha").AddOrReindexAsync(alpha, null, CancellationToken.None);
        await Service("model-a", "https://beta.example.com/b", "beta").AddOrReindexAsync(beta, null, CancellationToken.None);

        var docs = Service("model-a", "https://alpha.example.com/a", "alpha");
        await docs.LoadAsync(CancellationToken.None);
        await docs.AddOrReindexAsync(alpha, null, CancellationToken.None);

        Assert.Equal(0, docs.UnembeddedCount);
    }
}
