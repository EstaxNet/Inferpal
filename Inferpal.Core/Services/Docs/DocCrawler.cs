using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using Inferpal.Services.Tools;

namespace Inferpal.Services.Docs;

/// <summary>
/// Breadth-first crawler that walks a documentation site starting from a single URL,
/// following only same-domain links under the start URL's path prefix.
/// </summary>
/// <remarks>
/// Hard caps (<see cref="MaxPages"/> / <see cref="MaxDepth"/>) and a politeness delay keep
/// the crawl bounded and well-behaved. HTML→text conversion and the SSRF guard are reused
/// from <see cref="FetchUrlTool"/> so docs indexing shares the same hardening as the
/// <c>fetch_url</c> tool.
/// </remarks>
internal sealed class DocCrawler
{
    /// <summary>Maximum number of pages fetched in a single crawl.</summary>
    public const int MaxPages = 50;

    /// <summary>Maximum link depth from the start URL.</summary>
    public const int MaxDepth = 3;

    /// <summary>Delay between successive page fetches (politeness).</summary>
    private static readonly TimeSpan FetchDelay = TimeSpan.FromMilliseconds(100);

    private static readonly HttpClient _http = CreateClient();

    private static readonly Regex _hrefRegex = new(
        "href\\s*=\\s*[\"']([^\"'#]+)[\"']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex _titleRegex = new(
        @"<title[^>]*>([\s\S]*?)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // File extensions that are not crawlable documentation pages.
    private static readonly HashSet<string> _skipExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".ico",
            ".css", ".js", ".json", ".xml", ".rss",
            ".pdf", ".zip", ".gz", ".tar", ".mp4", ".webm", ".woff", ".woff2", ".ttf",
        };

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 };
        var client  = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        return client;
    }

    /// <summary>A single fetched documentation page.</summary>
    public readonly record struct Page(string Url, string Title, string Text);

    /// <summary>
    /// Crawls from <paramref name="startUrl"/> and returns the readable text of each visited page.
    /// </summary>
    /// <param name="progress">Reports <c>(pagesFetched, queuedTotal)</c> as the crawl proceeds.</param>
    public async Task<List<Page>> CrawlAsync(
        string startUrl,
        IProgress<(int fetched, int total)>? progress,
        CancellationToken ct)
    {
        var pages = new List<Page>();

        if (!Uri.TryCreate(startUrl, UriKind.Absolute, out var start) ||
            FetchUrlTool.IsPrivateOrLoopback(startUrl))
            return pages;

        var host        = start.Host;
        var pathPrefix  = PathPrefix(start);
        var visited     = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Normalize(start) };
        var queue       = new Queue<(Uri url, int depth)>();
        queue.Enqueue((start, 0));

        while (queue.Count > 0 && pages.Count < MaxPages)
        {
            ct.ThrowIfCancellationRequested();
            var (url, depth) = queue.Dequeue();

            string html;
            try
            {
                using var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) continue;

                var mediaType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) && mediaType.Length > 0)
                    continue;

                html = await resp.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { continue; }

            var text = FetchUrlTool.HtmlToText(html);
            if (!string.IsNullOrWhiteSpace(text))
                pages.Add(new Page(url.ToString(), ExtractTitle(html, url), text));

            progress?.Report((pages.Count, pages.Count + queue.Count));

            // Enqueue child links (until the page cap is reached).
            if (depth < MaxDepth)
            {
                foreach (var link in ExtractLinks(html, url, host, pathPrefix))
                {
                    if (visited.Count + queue.Count >= MaxPages * 4) break; // bound the frontier
                    if (visited.Add(link.normalized))
                        queue.Enqueue((link.uri, depth + 1));
                }
            }

            await Task.Delay(FetchDelay, ct);
        }

        return pages;
    }

    // ── Link extraction ──────────────────────────────────────────────────────

    private static IEnumerable<(Uri uri, string normalized)> ExtractLinks(
        string html, Uri pageUrl, string host, string pathPrefix)
    {
        foreach (Match m in _hrefRegex.Matches(html))
        {
            var raw = m.Groups[1].Value.Trim();
            if (raw.Length == 0 ||
                raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!Uri.TryCreate(pageUrl, raw, out var abs)) continue;

            if (abs.Scheme != Uri.UriSchemeHttp && abs.Scheme != Uri.UriSchemeHttps) continue;
            if (!abs.Host.Equals(host, StringComparison.OrdinalIgnoreCase)) continue;

            // Stay under the start URL's directory so we don't crawl the whole domain.
            if (!abs.AbsolutePath.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var ext = System.IO.Path.GetExtension(abs.AbsolutePath);
            if (ext.Length > 0 && _skipExtensions.Contains(ext)) continue;

            yield return (abs, Normalize(abs));
        }
    }

    /// <summary>Directory portion of the start URL's path (everything up to the last '/').</summary>
    private static string PathPrefix(Uri uri)
    {
        var path = uri.AbsolutePath;
        var slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..(slash + 1)];
    }

    /// <summary>Canonical key for dedup: scheme + host + path (query and fragment dropped).</summary>
    private static string Normalize(Uri uri) =>
        $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath.TrimEnd('/')}";

    private static string ExtractTitle(string html, Uri url)
    {
        var m = _titleRegex.Match(html);
        if (m.Success)
        {
            var title = WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, @"\s+", " ")).Trim();
            if (title.Length > 0) return title.Length > 200 ? title[..200] : title;
        }
        return url.ToString();
    }
}
