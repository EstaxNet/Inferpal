using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Inferpal.Services.Docs;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Whether an answer is a web page is decided as <c>fetch_url</c> decides it (<c>WebPage.IsHtml</c>): its media type when it
/// names one, its first characters when it names none or a generic one.
/// </summary>
public class DocsCrawlPageTypeTests
{
    private const string Html = "<!doctype html><html><head><title>Guide</title></head><body><p>Guide explained at length, with enough words.</p></body></html>";

    private sealed class OneAnswer(string body, string? mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            if (mediaType is not null) content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private static Task<List<DocCrawler.Page>> Crawl(string body, string? mediaType) =>
        new DocCrawler(new OneAnswer(body, mediaType), (_, _) => Task.FromResult(false))
            .CrawlAsync("https://docs.example.test/guide/", null, CancellationToken.None);

    [Fact]
    public async Task APageServedAsGenericBytes_IsAPage() =>
        Assert.Single(await Crawl(Html, "application/octet-stream"));

    [Fact]
    public async Task AnAnswerWithoutATypeThatIsNoPage_IsNotIndexed() =>
        Assert.Empty(await Crawl("{\"guide\": \"not a page\", \"words\": \"enough of them to be indexed as text\"}", null));

    [Fact]
    public async Task AnHtmlPage_IsAPage_AndAnotherType_IsNot()
    {
        // Reference arm: what the media type says, it decides.
        Assert.Single(await Crawl(Html, "text/html"));
        Assert.Empty(await Crawl(Html, "application/json"));
    }
}
