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

    private static HttpClient CreateClient() => WebPageClient.Create(TimeSpan.FromSeconds(20));

    public string Name        => "web_search";
    /// <inheritdoc/>
    public ToolEffect EffectOf(JsonElement args) => ToolEffect.Network;
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
        // The same clamp as the two search tools, on a list the model reads as the web's answer:
        // asked for 20 and served 10, "10 results" is indistinguishable from "the web has 10".
        var (max, maxNotice) = ClampedArgument.Read(args, "max_results", 5, 1, 10);

        // The query string is sent to an external search engine — a covert exfiltration channel for
        // a prompt-injected model. Gate it like fetch_url (session "always allow" keeps it unobtrusive).
        if (!await _approval.RequestApprovalAsync("web_search", query, ct))
            return Execution.ToolFailure.CancelledByUser;

        var url            = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}&kl=wt-wt";
        var (status, html) = await GetFollowingSameHostRedirectsAsync(new Uri(url), ct);

        return Render(status, html, max, maxNotice);
    }

    /// <summary>What one answer of the engine says — including when it searched nothing.</summary>
    /// <remarks>
    /// ⚠ The challenge is asked BEFORE parsing. DuckDuckGo answers a second search made within a few
    /// seconds with HTTP 202 and its anti-bot page, and keeps doing so for minutes — even for a
    /// query it answered with ten results just before. That page has no result, so it parsed as
    /// "No results.", the one sentence the model reads as a fact about the web: an agent refining
    /// its query concludes that nothing exists, on every search of the session after the first.
    /// </remarks>
    internal static string Render(int status, string html, int max, string? maxNotice)
    {
        if (IsChallenge(status, html))
            return Strings.WebSearchRefused;

        var results = ParseResults(html, max);
        if (results.Count == 0)
            return Strings.NoResults;

        return ClampedArgument.Above(maxNotice, string.Join("\n\n", results.Select((r, i) =>
            $"{i + 1}. {r.Title}\n   URL: {r.Url}\n   {r.Snippet}")));
    }

    /// <summary>DuckDuckGo's anti-bot check: a 2xx that is not an answer.</summary>
    internal static bool IsChallenge(int status, string html) =>
        status == (int)HttpStatusCode.Accepted
        || html.Contains("anomaly-modal", StringComparison.Ordinal);

    /// <summary>
    /// GETs <paramref name="uri"/>, following redirects <b>by hand</b> and only while they stay on
    /// DuckDuckGo over HTTPS. Turning the handler's automatic redirects off without this would have
    /// been a silent feature regression rather than a fix: the engine does redirect (locale, /html/
    /// path moves), and the tool would have parsed the redirect stub and reported "no results".
    /// </summary>
    internal static async Task<(int Status, string Html)> GetFollowingSameHostRedirectsAsync(Uri uri, CancellationToken ct, HttpClient? client = null)
    {
        for (var hop = 0; hop <= FetchUrlTool.MaxRedirects; hop++)
        {
            using var response = await (client ?? _http).GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);

            if ((int)response.StatusCode is < 300 or > 399 || response.Headers.Location is null)
            {
                response.EnsureSuccessStatusCode();
                return ((int)response.StatusCode, await WebPage.ReadTextAsync(response.Content, ct));
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
