using System.Net;
using System.Net.Http;
using System.Text;
using Inferpal.Services.Docs;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ Two failures of a <c>/docs</c> crawl read as something else. A page that did not answer within the crawler's
/// 30 s came back as an <see cref="OperationCanceledException"/> (how <c>HttpClient</c> reports its timeout), was
/// rethrown, and ended the WHOLE crawl — every page already fetched thrown away — under "indexing cancelled", when
/// nobody had cancelled anything. And a start address whose host does not resolve (a typo, an intranet name this
/// machine's DNS does not know) was refused by the guard as "private" and reported "no readable pages found", the
/// sentence of an empty site.
/// </summary>
public class DocsCrawlTimeoutAndNameTests
{
    private const string Root = "https://docs.example.test/guide/";

    /// <summary>Serves the start page; the slow page never answers in time (its budget runs out).</summary>
    private sealed class SlowPageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/guide/slow")
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
            var html = request.RequestUri.AbsolutePath == "/guide/"
                ? "<html><head><title>Home</title></head><body><p>Home explained at length.</p>"
                  + "<a href=\"/guide/slow\">slow</a><a href=\"/guide/fast\">fast</a></body></html>"
                : "<html><head><title>Fast</title></head><body><p>Fast explained at length.</p></body></html>";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html"),
            });
        }
    }

    [Fact]
    public async Task APageThatDoesNotAnswerInTime_IsSkippedAndCounted_NotACancellation()
    {
        var crawler = new DocCrawler(new SlowPageHandler(), (_, _) => Task.FromResult(false));

        var pages = await crawler.CrawlAsync(Root, null, CancellationToken.None);

        Assert.Equal(2, pages.Count);       // the start page and the fast one: the crawl went on
        Assert.Equal(1, crawler.TimedOut);
    }

    [Fact]
    public async Task ACancellationThatWasAsked_StillEndsTheCrawl()   // reference arm: the user's Stop is still a stop
    {
        var crawler = new DocCrawler(new SlowPageHandler(), (_, _) => Task.FromResult(false));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => crawler.CrawlAsync(Root, null, cts.Token));
    }

    [Fact]
    public void TheOutcome_NamesAHostThatDoesNotResolve_AndASiteThatDidNotAnswer()
    {
        var unresolved = DocsIndexService.DescribeCrawlOutcome("https://docs.pythn.org/", 0, refused: false, unresolved: true);
        Assert.Contains("could not be resolved", unresolved, StringComparison.Ordinal);
        Assert.DoesNotContain("private", unresolved, StringComparison.Ordinal);

        var slow = DocsIndexService.DescribeCrawlOutcome(Root, 0, refused: false, timedOut: 3);
        Assert.Contains("did not answer in time (3 page(s)", slow, StringComparison.Ordinal);

        Assert.Equal($"Docs: no readable pages found at {Root}.",          // reference arm: an empty site stays one
                     DocsIndexService.DescribeCrawlOutcome(Root, 0, refused: false));
        Assert.Contains("did not answer in time", DocsIndexService.TimeoutNote(2, "py"), StringComparison.Ordinal);
        Assert.Equal(string.Empty, DocsIndexService.TimeoutNote(0, "py"));
    }
}
