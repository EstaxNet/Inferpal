using System.IO;
using System.Linq;
using Inferpal.Config;
using Inferpal.Services.Docs;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The lexical half of the documentation search reads what NAMES a passage — its page title, its
/// section heading and its URL — not only its prose.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ The twin rule, written on the code index with its reason: <c>RagChunk.Bm25Tokens</c> is
/// <c>content + type name + relative path</c>, "so name-based queries score strongly". The
/// documentation index tokenized <b>the body alone</b> — while <c>search_docs</c> renders each row
/// as its <c>PageTitle</c> and its URL. The model is shown exactly the fields it cannot query.
/// </para>
/// <para>
/// ⚠ And it compounds with the embedding hole this corpus is documented to keep permanently: the
/// lexical side is the <b>only</b> route to a chunk held without a vector, so for those chunks the
/// title was not "weaker evidence", it was <b>no route at all</b>.
/// </para>
/// </remarks>
[Collection(DocsDatabaseCollection.Name)]
public sealed class DocsTitleSearchTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"docstitle-{Guid.NewGuid():N}");

    public DocsTitleSearchTests() => DocsDatabase.OverrideDirForTests = _dir;

    public void Dispose()
    {
        DocsDatabase.OverrideDirForTests = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>
    /// A page whose BODY never uses the word its title, heading and URL are built on — the ordinary
    /// shape of reference documentation, where the noun is in the heading and the prose says "it".
    /// </summary>
    private const string BodyWithoutTheWord =
        "Set the option in the client settings so the call is attempted again after a failure. " +
        "The client waits between attempts and gives up after the configured number. " +
        "Use a larger value on an unreliable link, and a smaller one when latency matters.";

    private static async Task<DocsIndexService> IndexedAsync(
        string url, string title, string body)
    {
        var docs = new DocsIndexService(new FakeInferenceProvider(), new InferpalConfig())
        {
            CrawlForTests = (_, _) => Task.FromResult(new List<DocCrawler.Page>
            {
                new(url, title, body),
            }),
        };
        await docs.AddOrReindexAsync(
            DocSite.Create("https://docs.example.com/", "Example"), progress: null, CancellationToken.None);
        return docs;
    }

    [Fact]
    public async Task AQueryUsingThePageTitle_FindsThePage_EvenWhenTheBodyNeverSaysIt()
    {
        var docs = await IndexedAsync("https://docs.example.com/guide", "Retries", BodyWithoutTheWord);

        // WITNESS: the fixture really is the case under test — the body does not carry the word, so
        // a hit can only come from the title.
        Assert.DoesNotContain("retries", BodyWithoutTheWord, StringComparison.OrdinalIgnoreCase);
        Assert.True(docs.ChunkCount > 0, "nothing was indexed: this test would measure nothing.");

        var hits = await docs.SearchAsync(null, "retries", 5, CancellationToken.None);

        Assert.NotEmpty(hits);
    }

    [Fact]
    public async Task AQueryUsingTheUrlPath_FindsThePage_EvenWhenTheBodyNeverSaysIt()
    {
        // The twin of the code index's relative path: on a documentation site the path segment is
        // very often the only place the topic is named in full.
        var docs = await IndexedAsync(
            "https://docs.example.com/reference/idempotency", "Guide", BodyWithoutTheWord);

        Assert.DoesNotContain("idempotency", BodyWithoutTheWord, StringComparison.OrdinalIgnoreCase);

        var hits = await docs.SearchAsync(null, "idempotency", 5, CancellationToken.None);

        Assert.NotEmpty(hits);
    }

    [Fact]
    public async Task AQueryUsingNoneOfThem_StillFindsNothing()
    {
        // REFERENCE ARM: without it, tokenizing "everything" would pass the two tests above while
        // turning the lexical half into a search that matches anything.
        var docs = await IndexedAsync("https://docs.example.com/guide", "Retries", BodyWithoutTheWord);

        var hits = await docs.SearchAsync(null, "kubernetes helm chart", 5, CancellationToken.None);

        Assert.Empty(hits);
    }
}
