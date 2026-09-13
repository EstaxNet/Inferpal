using System.IO;
using Inferpal.Config;
using Inferpal.Services.Docs;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>@Docs</c> without embeddings (model missing, circuit open): the fallback search.
/// </summary>
[Collection(DocsDatabaseCollection.Name)]
public class DocsKeywordFallbackTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"docs-{Guid.NewGuid():N}");

    public DocsKeywordFallbackTests() => DocsDatabase.OverrideDirForTests = _dir;

    public void Dispose()
    {
        DocsDatabase.OverrideDirForTests = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const string RetriesPage =
        "Retries. To configure retries, set the RetryCount option in the client settings. " +
        "The client waits between attempts and gives up after the configured number of retries. " +
        "Timeouts are configured separately with the RequestTimeout option.";

    private static async Task<DocsIndexService> IndexedAsync(string text)
    {
        var docs = new DocsIndexService(new FakeInferenceProvider(), new InferpalConfig())
        {
            CrawlForTests = async (_, _) =>
            {
                await Task.CompletedTask;
                return [new DocCrawler.Page("https://docs.example.com/retries", "Retries", text)];
            },
        };
        await docs.AddOrReindexAsync(DocSite.Create("https://docs.example.com/", "Example"), progress: null, CancellationToken.None);
        return docs;
    }

    /// <summary>
    /// The fallback searched the WHOLE query as a single substring, and <c>search_docs</c> passes the
    /// model's sentence: "how to configure retries" never found anything.
    /// </summary>
    [Fact]
    public async Task WithoutEmbeddings_ASentenceQuery_FindsThePageThatHasItsWords()
    {
        var docs = await IndexedAsync(RetriesPage);

        var hits = await docs.SearchAsync(null, "how to configure retries", 5, CancellationToken.None);

        Assert.NotEmpty(hits);
    }

    /// <summary>Witness: a query sharing no word with the page finds nothing.</summary>
    [Fact]
    public async Task WithoutEmbeddings_AnUnrelatedQuery_FindsNothing()
    {
        var docs = await IndexedAsync(RetriesPage);

        var hits = await docs.SearchAsync(null, "kubernetes helm chart", 5, CancellationToken.None);

        Assert.Empty(hits);
    }
}
