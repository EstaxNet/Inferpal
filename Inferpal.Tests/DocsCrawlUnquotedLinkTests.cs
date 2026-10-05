using System.Net;
using System.Net.Http;
using System.Text;
using Inferpal.Services.Docs;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  @Docs follows a link whatever its quoting.
//
//  The crawler's link pattern demanded quotes. HTML5 allows an unquoted attribute value, and a
//  minified site uses it everywhere: its crawl stopped at the start page — "✅ 1 pages" — and
//  search_docs answered "not in the documentation" for the rest of the site.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class DocsCrawlUnquotedLinkTests
{
    private const string Root = "https://docs.example.test/guide/";

    private sealed class SiteHandler(Dictionary<string, string> site) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!.AbsolutePath);
            var found = site.TryGetValue(request.RequestUri!.AbsolutePath, out var html);
            return Task.FromResult(new HttpResponseMessage(found ? HttpStatusCode.OK : HttpStatusCode.NotFound)
            {
                Content = new StringContent(html ?? "<html><body>not found</body></html>", Encoding.UTF8, "text/html"),
            });
        }
    }

    private static string Page(string title, string links) =>
        $"<html><head><title>{title}</title></head><body><p>{title} explained at length, with enough words.</p>{links}</body></html>";

    [Fact]
    public async Task AMinifiedSitesLinks_AreFollowed()
    {
        var handler = new SiteHandler(new()
        {
            ["/guide/"]             = Page("Home", "<a href=/guide/install.html>Install</a> <a class=nav href=retries.html#options>Retries</a> <a href='faq.html'>FAQ</a>"),
            ["/guide/install.html"] = Page("Install", ""),
            ["/guide/retries.html"] = Page("Retries", ""),
            ["/guide/faq.html"]     = Page("FAQ", ""),
        });
        var crawler = new DocCrawler(handler, (_, _) => Task.FromResult(false));

        var pages = await crawler.CrawlAsync(Root, null, CancellationToken.None);

        Assert.Contains(pages, p => p.Title == "FAQ");       // witness: a quoted link is followed
        Assert.Contains(pages, p => p.Title == "Install");
        Assert.Contains(pages, p => p.Title == "Retries");
    }

    [Fact]
    public async Task ACodeSampleOfALink_IsNotALink()
    {
        // Reference arm: a page that SHOWS the markup of a link (escaped) links nowhere.
        var handler = new SiteHandler(new()
        {
            ["/guide/"] = Page("Home", "<pre>&lt;a href=&quot;/guide/sample.html&quot;&gt;x&lt;/a&gt;</pre>"),
        });
        var crawler = new DocCrawler(handler, (_, _) => Task.FromResult(false));

        await crawler.CrawlAsync(Root, null, CancellationToken.None);

        Assert.DoesNotContain(handler.Requested, r => r.Contains("sample", StringComparison.Ordinal)
                                                    || r.Contains("quot", StringComparison.Ordinal));
    }
}
