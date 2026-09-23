using System.Net;
using System.Net.Http;
using System.Text;
using Inferpal.Services.Docs;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ Measured with the crawler's own headers: npmjs.com and medium.com answer 403 and a Cloudflare
/// challenge. A page the SITE refuses was dropped like a dead link, so `/docs add` on such a site
/// read "no readable pages found" — the sentence of an empty site, about pages that are full in the
/// user's browser — and a crawl throttled halfway read as a complete one.
/// </summary>
public class DocsCrawlRefusalTests
{
    private const string Root = "https://docs.example.test/guide/";

    /// <summary>Serves a fixed map of paths; anything else is a 404.</summary>
    private sealed class SiteHandler(Dictionary<string, (HttpStatusCode Status, string Html)> site) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (status, html) = site.TryGetValue(request.RequestUri!.AbsolutePath, out var page)
                ? page
                : (HttpStatusCode.NotFound, "<html><body>not found</body></html>");
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html"),
            });
        }
    }

    private static DocCrawler Crawler(Dictionary<string, (HttpStatusCode, string)> site) =>
        new(new SiteHandler(site), (_, _) => Task.FromResult(false));

    private static string Page(string title, params string[] links) =>
        $"<html><head><title>{title}</title></head><body><p>{title} explained at length.</p>"
        + string.Concat(links.Select(l => $"<a href=\"{l}\">{l}</a>")) + "</body></html>";

    [Fact]
    public async Task APageTheSiteRefuses_IsCounted_ADeadLinkIsNot()
    {
        var crawler = Crawler(new()
        {
            ["/guide/"]         = (HttpStatusCode.OK, Page("Home", "/guide/install", "/guide/config", "/guide/gone")),
            ["/guide/install"]  = (HttpStatusCode.OK, Page("Install")),
            ["/guide/config"]   = (HttpStatusCode.TooManyRequests, "<html><body>slow down</body></html>"),
            // /guide/gone: not served — a 404, a page that does not exist
        });

        var pages = await crawler.CrawlAsync(Root, null, CancellationToken.None);

        Assert.Equal(2, pages.Count);                                    // witness: the crawl ran
        Assert.Equal(new Dictionary<int, int> { [429] = 1 }, crawler.Refusals);
    }

    [Fact]
    public async Task ASiteThatRefusesItsFirstPage_IsNotDescribedAsEmpty()
    {
        var crawler = Crawler(new() { ["/guide/"] = (HttpStatusCode.Forbidden, "<html><body>Just a moment...</body></html>") });

        var pages   = await crawler.CrawlAsync(Root, null, CancellationToken.None);
        var outcome = DocsIndexService.DescribeCrawlOutcome(Root, pages.Count, refused: false, crawler.Refusals);

        Assert.Empty(pages);
        Assert.Contains("refused the crawler (HTTP 403)", outcome);
        Assert.DoesNotContain("no readable pages", outcome);
    }

    [Fact]
    public void AnEmptySite_KeepsItsSentence()
    {
        // Reference arm: nothing refused, nothing found — that one IS an empty site.
        var outcome = DocsIndexService.DescribeCrawlOutcome(Root, 0, refused: false, new Dictionary<int, int>());

        Assert.Contains("no readable pages found", outcome);
    }

    [Fact]
    public void AnIncompleteCrawl_SaysHowManyPagesWereWithheld_AndTheRemedy()
    {
        var note = DocsIndexService.RefusalNote(new Dictionary<int, int> { [429] = 5, [403] = 1 }, "npm-docs");

        Assert.Contains("6 page(s) refused by the site", note);
        Assert.Contains("HTTP 429 ×5, HTTP 403", note);
        Assert.Contains("/docs reindex npm-docs", note);
        Assert.Equal(string.Empty, DocsIndexService.RefusalNote(new Dictionary<int, int>(), "npm-docs")); // reference arm
    }
}
