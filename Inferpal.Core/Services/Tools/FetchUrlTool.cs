using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Tools;

internal class FetchUrlTool : ITool
{
    private const int MaxRedirects = 5;

    private readonly IApprovalService _approval;

    public FetchUrlTool(IApprovalService approval) => _approval = approval;

    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        // Redirects are followed MANUALLY (see ExecuteAsync) so each hop is re-checked
        // against IsPrivateOrLoopback — automatic redirects would let a public URL
        // bounce the request to 127.0.0.1 / 192.168.x.x and bypass the SSRF guard.
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client  = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        return client;
    }

    public string Name        => "fetch_url";
    public string Description => "Fetches the readable text content of a web page. Use this to read documentation, articles, or any web page whose URL you already know.";
    public object Parameters  => new
    {
        type = "object",
        properties = new
        {
            url       = new { type = "string",  description = "Full URL to fetch (e.g. https://example.com/page)." },
            max_chars = new { type = "integer", description = "Maximum characters to return (default 8000, max 50000)." }
        },
        required = new[] { "url" }
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var url      = args.GetProperty("url").GetString() ?? throw new ArgumentException("url required");
        var maxChars = args.TryGetProperty("max_chars", out var mc) ? mc.GetInt32() : 8000;
        maxChars     = Math.Clamp(maxChars, 500, 50_000);

        // Outbound network = exfiltration channel. Gate it like the other side-effecting tools so a
        // prompt-injected model can't silently ship workspace data off-machine (session "always allow").
        if (!await _approval.RequestApprovalAsync("fetch_url", url, ct))
            return "Cancelled by user.";

        var html = await GetStringCheckingRedirectsAsync(url, ct);
        var text = HtmlToText(html);

        return text.Length <= maxChars
            ? text
            : text[..maxChars] + $"\n\n[... truncated to {maxChars} characters out of {text.Length} total]";
    }

    /// <summary>
    /// GETs <paramref name="url"/>, following up to <see cref="MaxRedirects"/> redirects
    /// manually and re-validating EVERY hop against <see cref="IsPrivateOrLoopback"/>.
    /// </summary>
    private static async Task<string> GetStringCheckingRedirectsAsync(string url, CancellationToken ct)
    {
        var current = url;
        for (int hop = 0; ; hop++)
        {
            // Two-stage SSRF guard: the literal-IP check, then a DNS resolution of host NAMES.
            // The latter closes the DNS-rebinding gap — a public name whose A record points at
            // 127.0.0.1 / 169.254.169.254 passes the literal check but is caught here.
            if (IsPrivateOrLoopback(current) || await ResolvesToPrivateAsync(current, ct))
                throw new ArgumentException(
                    hop == 0
                        ? "Access denied: fetching private/loopback addresses is not allowed. " +
                          "Only public internet URLs are permitted."
                        : $"Access denied: the page redirected to a private/loopback address ({current}), " +
                          "which is not allowed.");

            using var response = await _http.GetAsync(current, ct);

            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                if (hop >= MaxRedirects)
                    throw new ArgumentException($"Too many redirects (more than {MaxRedirects}).");
                current = location.IsAbsoluteUri
                    ? location.ToString()
                    : new Uri(new Uri(current), location).ToString();
                continue;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        }
    }

    /// <summary>
    /// Returns <c>true</c> for URLs targeting loopback, private RFC-1918 ranges,
    /// or link-local addresses (SSRF prevention).
    /// </summary>
    internal static bool IsPrivateOrLoopback(string url)
    {
        try
        {
            var uri = new Uri(url);

            // Reject non-HTTP(S) schemes (file://, ftp://, etc.)
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return true;

            var host = uri.Host;

            // Well-known loopback / metadata names
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase))
                return true;

            // Parse as IP literal for range checks. Host NAMES are resolved separately by
            // ResolvesToPrivateAsync (kept off this method so it stays pure/synchronous/testable).
            return IPAddress.TryParse(host, out var ip) && IsPrivateIp(ip);
        }
        catch
        {
            // If URI parsing fails, err on the side of caution and block.
            return true;
        }
    }

    /// <summary>Range check for a single resolved IP (loopback, RFC-1918, link-local, CGNAT, …).</summary>
    private static bool IsPrivateIp(IPAddress ip)
    {
        // Unwrap IPv4-mapped IPv6 (::ffff:127.0.0.1) so it can't smuggle a private v4 address.
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 0                                     // 0.0.0.0/8    "this host"
                || b[0] == 127                                   // 127.0.0.0/8  loopback
                || b[0] == 10                                    // 10.0.0.0/8   RFC-1918
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)   // 172.16.0.0/12 RFC-1918
                || (b[0] == 192 && b[1] == 168)                 // 192.168.0.0/16 RFC-1918
                || (b[0] == 169 && b[1] == 254)                 // 169.254.0.0/16 link-local
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127); // 100.64.0.0/10 CGNAT
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.Equals(IPAddress.IPv6Loopback) || ip.Equals(IPAddress.IPv6Any)) return true;
            var b = ip.GetAddressBytes();
            return ((b[0] & 0xFE) == 0xFE && (b[1] & 0xC0) == 0x80) // fe80::/10 link-local
                || (b[0] & 0xFE) == 0xFC;                            // fc00::/7  unique-local (ULA)
        }

        return false;
    }

    /// <summary>
    /// Resolves the host NAME via DNS and returns <c>true</c> if any returned address is
    /// private/loopback. Closes the DNS-rebinding gap that <see cref="IsPrivateOrLoopback"/>
    /// (IP-literal only) leaves open. IP literals are already covered, so they are skipped here.
    /// </summary>
    private static async Task<bool> ResolvesToPrivateAsync(string url, CancellationToken ct)
    {
        try
        {
            var host = new Uri(url).Host;
            if (IPAddress.TryParse(host, out _)) return false; // literal — already validated

            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            return addresses.Length == 0 || addresses.Any(IsPrivateIp);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // DNS failure / malformed URI: block rather than risk an unvalidated request.
            return true;
        }
    }

    // fetch_url runs these regexes over attacker-controlled HTML. A bounded match timeout
    // turns a pathological backtracking input (ReDoS) into a caught exception instead of a hang.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private static string ReplaceBounded(string input, string pattern, string replacement, RegexOptions options) =>
        Regex.Replace(input, pattern, replacement, options, RegexTimeout);

    internal static string HtmlToText(string html)
    {
        try
        {
            // Remove non-content blocks entirely
            html = ReplaceBounded(html,
                @"<(script|style|noscript|head|nav|footer|header|aside|iframe)[^>]*>[\s\S]*?</\1>",
                "", RegexOptions.IgnoreCase);

            // Block-level elements → newlines before content collapses
            html = ReplaceBounded(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            html = ReplaceBounded(html,
                @"</(p|div|li|dt|dd|tr|th|td|h[1-6]|blockquote|pre|article|section|figure)>",
                "\n", RegexOptions.IgnoreCase);

            // Strip all remaining tags
            html = ReplaceBounded(html, @"<[^>]+>", "", RegexOptions.None);

            // Decode HTML entities
            html = WebUtility.HtmlDecode(html);

            // Normalize whitespace
            html = ReplaceBounded(html, @"[^\S\n]+", " ",    RegexOptions.None); // tabs/spaces → single space
            html = ReplaceBounded(html, @"\n[ \t]+", "\n",   RegexOptions.None); // trim leading spaces on lines
            html = ReplaceBounded(html, @"\n{3,}",   "\n\n", RegexOptions.None); // 3+ blank lines → 2

            return html.Trim();
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological markup: fall back to a linear, backtracking-free tag strip.
            var sb = new StringBuilder(html.Length);
            bool inTag = false;
            foreach (var c in html)
            {
                if (c == '<') inTag = true;
                else if (c == '>') inTag = false;
                else if (!inTag) sb.Append(c);
            }
            return WebUtility.HtmlDecode(sb.ToString()).Trim();
        }
    }
}
