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
        var handler = new HttpClientHandler { AllowAutoRedirect = true };
        var client  = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
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
        var query = args.GetProperty("query").GetString() ?? throw new ArgumentException("query required");
        var max   = Math.Clamp(
            args.TryGetProperty("max_results", out var mr) ? mr.GetInt32() : 5, 1, 10);

        // The query string is sent to an external search engine — a covert exfiltration channel for
        // a prompt-injected model. Gate it like fetch_url (session "always allow" keeps it unobtrusive).
        if (!await _approval.RequestApprovalAsync("web_search", query, ct))
            return "Cancelled by user.";

        var url  = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}&kl=wt-wt";
        var html = await _http.GetStringAsync(url, ct);

        var results = ParseResults(html, max);
        if (results.Count == 0)
            return Strings.NoResults;

        return string.Join("\n\n", results.Select((r, i) =>
            $"{i + 1}. {r.Title}\n   URL: {r.Url}\n   {r.Snippet}"));
    }

    private static List<(string Title, string Url, string Snippet)> ParseResults(string html, int max)
    {
        var results = new List<(string, string, string)>();

        // Match result title links (href comes before or after class attribute)
        var titleRx = new Regex(
            @"<a[^>]+class=""result__a""[^>]*href=""([^""]*)""[^>]*>([\s\S]*?)</a>" +
            @"|<a[^>]+href=""([^""]*)""[^>]+class=""result__a""[^>]*>([\s\S]*?)</a>",
            RegexOptions.IgnoreCase);

        // Snippet: <div class="result__snippet"> or <a class="result__snippet">
        var snippetRx = new Regex(
            @"class=""result__snippet""[^>]*>([\s\S]*?)</(?:div|a)>",
            RegexOptions.IgnoreCase);

        var titles   = titleRx.Matches(html);
        var snippets = snippetRx.Matches(html);

        for (int i = 0; i < titles.Count && results.Count < max; i++)
        {
            var tm = titles[i];

            // Two capture group pairs for the two regex alternatives
            var rawHref = tm.Groups[1].Success ? tm.Groups[1].Value : tm.Groups[3].Value;
            var rawText = tm.Groups[2].Success ? tm.Groups[2].Value : tm.Groups[4].Value;

            rawHref = WebUtility.HtmlDecode(rawHref);
            var title = WebUtility.HtmlDecode(Regex.Replace(rawText, @"<[^>]+>", "")).Trim();
            var url   = DecodeUrl(rawHref);

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url)) continue;
            // Skip DuckDuckGo internal links
            if (url.Contains("duckduckgo.com") && !url.StartsWith("http")) continue;

            // Nearest snippet after this title's position
            var snippetMatch = snippets.Cast<Match>()
                .FirstOrDefault(s => s.Index > tm.Index && s.Index < tm.Index + 3000);
            var snippet = snippetMatch is not null
                ? WebUtility.HtmlDecode(Regex.Replace(snippetMatch.Groups[1].Value, @"<[^>]+>", "")).Trim()
                : "";

            results.Add((title, url, snippet));
        }

        return results;
    }

    private static string DecodeUrl(string href)
    {
        // DuckDuckGo redirects: /l/?uddg=https%3A%2F%2F...&rut=...
        var m = Regex.Match(href, @"[?&]uddg=([^&]+)", RegexOptions.IgnoreCase);
        if (m.Success)
            return Uri.UnescapeDataString(m.Groups[1].Value);

        // Relative links → skip
        if (href.StartsWith("/") && !href.StartsWith("//"))
            return "";

        return href;
    }
}
