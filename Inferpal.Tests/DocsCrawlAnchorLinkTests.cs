using System.Net;
using System.Net.Http;
using System.Text;
using Inferpal.Services.Docs;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The crawler's link pattern stopped at a '#' and then demanded the closing quote, so a link to a SECTION of another
/// page — <c>href="retries.html#options"</c>, the shape of every Sphinx cross-reference — matched nothing: a page
/// reached only that way was never indexed, and <c>@Docs</c> answered as if the site had no such page.
/// </summary>
public class DocsCrawlAnchorLinkTests
{
    private const string Root = "https://docs.example.test/guide/";

    private sealed class SiteHandler(Dictionary<string, string> site) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!.PathAndQuery);
            var found = site.TryGetValue(request.RequestUri!.AbsolutePath, out var html);
            return Task.FromResult(new HttpResponseMessage(found ? HttpStatusCode.OK : HttpStatusCode.NotFound)
            {
                Content = new StringContent(html ?? "<html><body>not found</body></html>", Encoding.UTF8, "text/html"),
            });
        }
    }

    private static string Page(string title, params string[] links) =>
        $"<html><head><title>{title}</title></head><body><p>{title} explained at length, with enough words.</p>"
        + string.Concat(links.Select(l => $"<a href=\"{l}\">{l}</a>")) + "</body></html>";

    [Fact]
    public async Task APageLinkedOnlyToOneOfItsSections_IsCrawled_Once()
    {
        var handler = new SiteHandler(new()
        {
            ["/guide/"]             = Page("Home", "retries.html#options", "install.html", "retries.html#defaults"),
            ["/guide/install.html"] = Page("Install"),
            ["/guide/retries.html"] = Page("Retries"),
        });
        var crawler = new DocCrawler(handler, (_, _) => Task.FromResult(false));

        var pages = await crawler.CrawlAsync(Root, null, CancellationToken.None);

        Assert.Contains(pages, p => p.Title == "Install");                       // witness: plain links are followed
        Assert.Contains(pages, p => p.Title == "Retries");
        Assert.Single(handler.Requested, r => r == "/guide/retries.html");      // two anchors, one page, one fetch
    }

    [Fact]
    public async Task AnAnchorWithinThePage_FetchesNothing()
    {
        // Reference arm: "#top" is the page itself.
        var handler = new SiteHandler(new() { ["/guide/"] = Page("Home", "#top", "#install") });
        var crawler = new DocCrawler(handler, (_, _) => Task.FromResult(false));

        var pages = await crawler.CrawlAsync(Root, null, CancellationToken.None);

        Assert.Single(pages);
        Assert.Equal(["/guide/"], handler.Requested);
    }
}
