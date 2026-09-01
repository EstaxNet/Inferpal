using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

internal class WebSearchTool : ITool
{
    private readonly IApprovalService _approval;

    public WebSearchTool(IApprovalService approval) => _approval = approval;

    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        // The same two guarantees as the other two HTTP clients in this repository, for the same
        // reasons — and they were written there and not here, which is the whole defect: three
        // clients, one invariant, stated twice (revue post-1.6.0, item 4.1).
        //
        //  · No automatic redirect. `FetchUrlTool` and `DocCrawler` both say it in the same words:
        //    an automatic redirect lets a public URL bounce the request onto 127.0.0.1 or
        //    169.254.169.254 without passing the SSRF guard again. Here the host is fixed and the
        //    query escaped, so exploiting it takes a redirect served by DuckDuckGo itself — a thin
        //    risk, but the cost of closing it is a flag, and the cost of leaving it is that the
        //    next reader of these three files cannot tell which of the two rules is the real one.
        //  · A response ceiling. Without it the whole body is buffered before any truncation.
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client  = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20),
            MaxResponseContentBufferSize = 8 * 1024 * 1024,
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        return client;
    }

    public string Name        => "web_search";
    public string Description => "Searches the internet using DuckDuckGo and returns a list of results with title, URL, and snippet. Use this to find up-to-date information, documentation, or answers to factual questions.";
    public object Parameters  => new
    {
        type = "object",
        properties = new
        {
            query       = new { type = "string",  description = "Search query." },
            max_results = new { type = "integer", description = "Number of results to return (default 5, max 10)." }
        },
        required = new[] { "query" }
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var query = args.Str("query") ?? throw new ArgumentException("query is required.");
        var max   = Math.Clamp(
            args.Int("max_results", 5), 1, 10);

        // The query string is sent to an external search engine — a covert exfiltration channel for
        // a prompt-injected model. Gate it like fetch_url (session "always allow" keeps it unobtrusive).
        if (!await _approval.RequestApprovalAsync("web_search", query, ct))
            return "Cancelled by user.";

        var url  = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}&kl=wt-wt";
        var html = await GetFollowingSameHostRedirectsAsync(new Uri(url), ct);

        var results = ParseResults(html, max);
        if (results.Count == 0)
            return Strings.NoResults;

        return string.Join("\n\n", results.Select((r, i) =>
            $"{i + 1}. {r.Title}\n   URL: {r.Url}\n   {r.Snippet}"));
    }

    /// <summary>
    /// GETs <paramref name="uri"/>, following redirects <b>by hand</b> and only while they stay on
    /// DuckDuckGo over HTTPS. Turning the handler's automatic redirects off without this would have
    /// been a silent feature regression rather than a fix: the engine does redirect (locale, /html/
    /// path moves), and the tool would have parsed the redirect stub and reported "no results".
    /// </summary>
    private static async Task<string> GetFollowingSameHostRedirectsAsync(Uri uri, CancellationToken ct)
    {
        for (var hop = 0; hop < 4; hop++)
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);

            if ((int)response.StatusCode is < 300 or > 399 || response.Headers.Location is null)
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(ct);
            }

            var next = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(uri, response.Headers.Location);

            if (next.Scheme != Uri.UriSchemeHttps ||
                !(next.Host.Equals("duckduckgo.com", StringComparison.OrdinalIgnoreCase) ||
                  next.Host.EndsWith(".duckduckgo.com", StringComparison.OrdinalIgnoreCase)))
                throw new HttpRequestException($"web_search refused a redirect off the search engine: {next.Scheme}://{next.Host}");

            uri = next;
        }
        throw new HttpRequestException("web_search: too many redirects.");
    }

    private static List<(string Title, string Url, string Snippet)> ParseResults(string html, int max)
    {
        var results = new List<(string, string, string)>();

        // Match result title links (href comes before or after class attribute)
        var titleRx = new Regex(
            @"<a[^>]+class=""result__a""[^>]*href=""([^""]*)""[^>]*>([\s\S]*?)</a>" +
            @"|<a[^>]+href=""([^""]*)""[^>]+class=""result__a""[^>]*>([\s\S]*?)</a>",
            RegexOptions.IgnoreCase, RegexBudget.Default);

        // Snippet: <div class="result__snippet"> or <a class="result__snippet">
        var snippetRx = new Regex(
            @"class=""result__snippet""[^>]*>([\s\S]*?)</(?:div|a)>",
            RegexOptions.IgnoreCase, RegexBudget.Default);

        var titles   = titleRx.Matches(html);
        var snippets = snippetRx.Matches(html);

        for (int i = 0; i < titles.Count && results.Count < max; i++)
        {
            var tm = titles[i];

            // Two capture group pairs for the two regex alternatives
            var rawHref = tm.Groups[1].Success ? tm.Groups[1].Value : tm.Groups[3].Value;
            var rawText = tm.Groups[2].Success ? tm.Groups[2].Value : tm.Groups[4].Value;

            rawHref = WebUtility.HtmlDecode(rawHref);
            // Budgeted like every other pattern in this file: these two run on EXTERNAL HTML.
            var title = WebUtility.HtmlDecode(Regex.Replace(rawText, @"<[^>]+>", "", RegexOptions.None, RegexBudget.Default)).Trim();
            var url   = DecodeUrl(rawHref);

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url)) continue;
            // Skip DuckDuckGo internal links
            if (url.Contains("duckduckgo.com") && !url.StartsWith("http")) continue;

            // Nearest snippet after this title's position
            var snippetMatch = snippets.Cast<Match>()
                .FirstOrDefault(s => s.Index > tm.Index && s.Index < tm.Index + 3000);
            var snippet = snippetMatch is not null
                ? WebUtility.HtmlDecode(Regex.Replace(snippetMatch.Groups[1].Value, @"<[^>]+>", "", RegexOptions.None, RegexBudget.Default)).Trim()
                : "";

            results.Add((title, url, snippet));
        }

        return results;
    }

    private static string DecodeUrl(string href)
    {
        // DuckDuckGo redirects: /l/?uddg=https%3A%2F%2F...&rut=...
        var m = Regex.Match(href, @"[?&]uddg=([^&]+)", RegexOptions.IgnoreCase, RegexBudget.Default);
        if (m.Success)
            return Uri.UnescapeDataString(m.Groups[1].Value);

        // Relative links → skip
        if (href.StartsWith("/") && !href.StartsWith("//"))
            return "";

        return href;
    }
}
