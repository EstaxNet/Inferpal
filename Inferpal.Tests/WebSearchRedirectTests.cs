using System.Net;
using System.Net.Http;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The search engine's redirects are followed as far as every other address from outside (<c>FetchUrlTool.MaxRedirects</c>):
/// <c>web_search</c> stopped after three, where <c>fetch_url</c> and the docs crawler follow five.
/// </summary>
public class WebSearchRedirectTests
{
    /// <summary>The engine's own redirects (locale, a moved path), <paramref name="hops"/> of them, then the results page.</summary>
    private sealed class Redirects(int hops) : HttpMessageHandler
    {
        private int _served;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (_served++ < hops)
            {
                var moved = new HttpResponseMessage(HttpStatusCode.Found);
                moved.Headers.Location = new Uri($"https://html.duckduckgo.com/html/?hop={_served}");
                return Task.FromResult(moved);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>results</html>") });
        }
    }

    [Fact]
    public async Task FourRedirectsOfTheEngine_AreFollowed()
    {
        using var client = new HttpClient(new Redirects(4));

        var (status, html) = await WebSearchTool.GetFollowingSameHostRedirectsAsync(new Uri("https://html.duckduckgo.com/html/?q=x"), default, client);

        Assert.Equal(200, status);
        Assert.Contains("results", html);
    }

    [Fact]
    public async Task MoreRedirectsThanAnyAddressMayTake_AreRefused() =>
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            WebSearchTool.GetFollowingSameHostRedirectsAsync(new Uri("https://html.duckduckgo.com/html/?q=x"), default,
                                                             new HttpClient(new Redirects(FetchUrlTool.MaxRedirects + 1))));
}
