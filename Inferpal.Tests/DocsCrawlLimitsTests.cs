using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Inferpal.Config;
using Inferpal.Services.Docs;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>@Docs</c>: a crawl stopped by its depth or its link frontier says so, like one stopped by its page cap.
/// </summary>
/// <remarks>
/// Only the page cap was said. A site deeper than the crawl's depth, or whose pages carry more links than its frontier
/// holds, ended under "✅ N pages", and <c>@Docs</c> answered "not in the documentation" about pages never fetched. The
/// frontier also counted every queued link twice and closed at half its bound.
/// </remarks>
[Collection(DocsDatabaseCollection.Name)]
public sealed class DocsCrawlLimitsTests : IDisposable
{
    private const string Root = "https://docs.example.test/guide/";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"docslimits-{Guid.NewGuid():N}");

    public DocsCrawlLimitsTests() => DocsDatabase.OverrideDirForTests = _dir;

    public void Dispose()
    {
        DocsDatabase.OverrideDirForTests = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class SiteHandler(Dictionary<string, string> site) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var found = site.TryGetValue(request.RequestUri!.AbsolutePath, out var html);
            return Task.FromResult(new HttpResponseMessage(found ? HttpStatusCode.OK : HttpStatusCode.NotFound)
            {
                Content = new StringContent(html ?? "<html><body>not found</body></html>", Encoding.UTF8, "text/html"),
            });
        }
    }

    private static string Page(string title, params string[] links) =>
        $"<html><head><title>{title}</title></head><body><p>{title} explained at length, with enough words to index.</p>"
        + string.Concat(links.Select(l => $"<a href=\"{l}\">{l}</a>")) + "</body></html>";

    /// <summary>Home → a → b → c → d: five levels, one more than the crawl follows.</summary>
    private static Dictionary<string, string> DeepSite() => new()
    {
        ["/guide/"]       = Page("Home", "a.html"),
        ["/guide/a.html"] = Page("A", "b.html"),
        ["/guide/b.html"] = Page("B", "c.html"),
        ["/guide/c.html"] = Page("C", "d.html"),
        ["/guide/d.html"] = Page("D"),
    };

    private static DocCrawler Crawler(Dictionary<string, string> site) =>
        new(new SiteHandler(site), (_, _) => Task.FromResult(false));

    [Fact]
    public async Task ALinkPastTheDepth_IsCounted()
    {
        var crawler = Crawler(DeepSite());
        var pages   = await crawler.CrawlAsync(Root, null, CancellationToken.None);

        Assert.Contains(pages, p => p.Title == "C");                 // witness: the crawl went as deep as it may
        Assert.DoesNotContain(pages, p => p.Title == "D");
        Assert.Equal(1, crawler.BeyondDepth);
        Assert.Equal(0, crawler.BeyondFrontier);
    }

    [Fact]
    public async Task TheFrontier_HoldsItsBound_AndCountsWhatItLeaves()
    {
        var links   = Enumerable.Range(0, 250).Select(i => $"p{i}.html").ToArray();
        var crawler = Crawler(new() { ["/guide/"] = Page("Home", links) });

        await crawler.CrawlAsync(Root, null, CancellationToken.None);

        // The start page and 199 links fill the 200 known links; the other 51 are counted.
        Assert.Equal(250 - (DocCrawler.MaxKnownLinks - 1), crawler.BeyondFrontier);
    }

    [Fact]
    public void TheNote_NamesEachBound_AndNothingWhenTheSiteRanOut()
    {
        Assert.Contains("crawl limit of", DocsIndexService.CrawlLimitNote(DocCrawler.MaxPages, 3, 3), StringComparison.Ordinal);
        Assert.Contains("1 link(s) deeper than", DocsIndexService.CrawlLimitNote(4, 1, 0), StringComparison.Ordinal);
        Assert.Contains("51 link(s) not followed", DocsIndexService.CrawlLimitNote(1, 0, 51), StringComparison.Ordinal);
        Assert.Equal(string.Empty, DocsIndexService.CrawlLimitNote(4, 0, 0));    // reference arm: a site crawled to its end
    }

    [Fact]
    public async Task TheIndexStatus_SaysTheSiteMayHaveMore()
    {
        var service = new DocsIndexService(new FakeInferenceProvider { OnEmbedding = _ => [1f, 0f] },
                                           new InferpalConfig { RagEmbeddingModel = "embed" })
        {
            CrawlerForTests = () => Crawler(DeepSite()),
        };

        await service.AddOrReindexAsync(DocSite.Create(Root, "Guide"), null, CancellationToken.None);

        Assert.Contains("4 pages", service.Status, StringComparison.Ordinal);           // witness: the pass indexed
        Assert.Contains("1 link(s) deeper than", service.Status, StringComparison.Ordinal);
    }
}
