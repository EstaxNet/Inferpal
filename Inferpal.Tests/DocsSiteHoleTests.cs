using System.IO;
using Inferpal.Config;
using Inferpal.Services.Docs;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The status after indexing a documentation source counts THAT source's chunks without a vector — against its own
/// chunks — and says the other sources' apart.
/// </summary>
/// <remarks>
/// ⚠ The hole note took the corpus-wide counts: "Vue — 120 chunks (50 of 520 chunks without embedding)" for a Vue fully
/// embedded, the 50 being React's — and its remedy pointed at the wrong source.
/// </remarks>
[Collection(DocsDatabaseCollection.Name)]
public sealed class DocsSiteHoleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"docs-site-hole-{Guid.NewGuid():N}");

    public DocsSiteHoleTests() => DocsDatabase.OverrideDirForTests = _dir;

    public void Dispose()
    {
        DocsDatabase.OverrideDirForTests = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static string Page(string marker) => string.Join(" ",
        Enumerable.Range(0, 4_000).Select(i => i % 40 == 0 ? marker : $"word{i % 17}"));

    [Fact]
    public async Task ASourceFullyEmbedded_IsNotBlamedForAnotherSourcesHole()
    {
        var react = DocSite.Create("https://react.example/", "React");
        var vue   = DocSite.Create("https://vue.example/", "Vue");
        var embedAll = false;
        var served = 0;
        var docs = new DocsIndexService(
            new FakeInferenceProvider { OnEmbedding = _ => embedAll || served++ < 1 ? [0.1f, 0.2f] : null },
            new InferpalConfig { RagEmbeddingModel = "embed-model" })
        {
            CrawlForTests = (url, _) => Task.FromResult(new List<DocCrawler.Page>
            {
                new(url + "a", "One", Page("alpha")),
                new(url + "b", "Two", Page("beta")),
            }),
        };

        await docs.AddOrReindexAsync(react, progress: null, CancellationToken.None);
        var reactHole = docs.UnembeddedBySite.TryGetValue(react.Id, out var h) ? h : 0;
        Assert.True(reactHole > 0, "the first source has no hole: this test reads nothing");   // WITNESS
        Assert.Contains($"run /docs reindex {react.Id})", docs.Status);                       // reference arm: its own hole

        embedAll = true;
        await docs.AddOrReindexAsync(vue, progress: null, CancellationToken.None);

        Assert.StartsWith($"Docs: ✅ {vue.Title}", docs.Status);
        Assert.DoesNotContain($"run /docs reindex {vue.Id})", docs.Status);   // not this source's hole
        Assert.Contains($"(other sources: {reactHole} of", docs.Status);      // said apart, with its count
    }
}
