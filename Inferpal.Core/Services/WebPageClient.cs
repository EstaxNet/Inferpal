using System.Net.Http;

namespace Inferpal.Services;

/// <summary>
/// The HTTP client of everything that reads a web page nobody here controls — <c>fetch_url</c>, <c>web_search</c>, the
/// <c>@Docs</c> crawler: one client, so its two guarantees are written once.
/// </summary>
/// <remarks>
/// ⚠ No automatic redirect: one would let a public URL bounce the request onto 127.0.0.1 or 169.254.169.254 without
/// passing the SSRF guard again; each caller follows a redirect by hand and judges its target. ⚠ A response ceiling:
/// without it the whole body is buffered before any truncation, and one oversized (or hostile) page is read entirely into
/// the process that hosts the chat.
/// </remarks>
internal static class WebPageClient
{
    /// <summary>The most of a response read.</summary>
    internal const int MaxResponseBytes = 8 * 1024 * 1024;

    /// <summary>A client that gives up after <paramref name="timeout"/>.</summary>
    internal static HttpClient Create(TimeSpan timeout)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, Proxy = NetworkProxy.Shared };
        var client  = new HttpClient(handler) { Timeout = timeout, MaxResponseContentBufferSize = MaxResponseBytes };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        return client;
    }
}
