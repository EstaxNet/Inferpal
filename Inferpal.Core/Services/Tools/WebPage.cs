using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Tools;

/// <summary>The text of a fetched web page, decoded in the encoding the page declares.</summary>
/// <remarks>
/// ⚠ <c>ReadAsStringAsync</c> knows only the HTTP header's charset. A static page is commonly served as plain
/// <c>text/html</c> — nginx's and Apache's default — and declares its encoding in a <c>&lt;meta&gt;</c>: read as
/// UTF-8, a GBK, Shift_JIS or ISO-8859-2 page reaches the model as mojibake. And a header naming a charset .NET does
/// not know makes <c>ReadAsStringAsync</c> throw, so the whole fetch fails. The order is the browsers': byte order
/// mark, then header, then a <c>&lt;meta&gt;</c> in the first 1024 bytes, then UTF-8. Shared by fetch_url, @Docs
/// and web_search.
/// </remarks>
internal static class WebPage
{
    static WebPage() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>How far into the page a <c>&lt;meta&gt;</c> charset is looked for, as browsers do.</summary>
    private const int MetaPrescanBytes = 1024;

    // Both forms: <meta charset="gbk"> and <meta http-equiv="Content-Type" content="text/html; charset=gbk">.
    private static readonly Regex MetaCharset = new(
        @"<meta\b[^>]*?charset\s*=\s*[""']?\s*([A-Za-z0-9_.:\-]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexBudget.Default);

    /// <summary>A response's text and the media type the server gave it (<c>null</c> when it gave none).</summary>
    internal readonly record struct WebText(string Text, string? MediaType);

    internal static async Task<string> ReadTextAsync(HttpContent content, CancellationToken ct) =>
        (await ReadAsync(content, ct)).Text;

    internal static async Task<WebText> ReadAsync(HttpContent content, CancellationToken ct)
    {
        var bytes = await content.ReadAsByteArrayAsync(ct);
        return new WebText(Decode(bytes, content.Headers.ContentType?.CharSet), content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// Whether a response is a web PAGE: its media type says so, or — when it says nothing — its first characters do.
    /// </summary>
    internal static bool IsHtml(WebText page)
    {
        if (page.MediaType is { Length: > 0 } type && !type.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
            return type.Equals("text/html", StringComparison.OrdinalIgnoreCase)
                || type.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
        var head = page.Text.AsSpan(0, Math.Min(page.Text.Length, 1024)).TrimStart();
        return head.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            || head.Contains("<body", StringComparison.OrdinalIgnoreCase);
    }

    internal static string Decode(byte[] bytes, string? headerCharset)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        var encoding = ByName(headerCharset) ?? MetaEncoding(bytes) ?? Encoding.UTF8;
        return encoding.GetString(bytes);
    }

    private static Encoding? MetaEncoding(byte[] bytes)
    {
        // ASCII is enough to find the declaration: every encoding a page may declare this way is ASCII-compatible.
        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, MetaPrescanBytes));
        Match match;
        try { match = MetaCharset.Match(head); }
        catch (RegexMatchTimeoutException) { return null; }
        if (!match.Success) return null;

        // A page this prescan could read is not UTF-16, whatever it says: browsers read such a declaration as UTF-8.
        var declared = ByName(match.Groups[1].Value);
        return declared is UnicodeEncoding or UTF32Encoding ? Encoding.UTF8 : declared;
    }

    /// <summary>The encoding a charset label names; <c>null</c> for no label, or one .NET does not know.</summary>
    private static Encoding? ByName(string? label)
    {
        label = label?.Trim().Trim('"', '\'').Trim();
        if (string.IsNullOrEmpty(label)) return null;
        try { return Encoding.GetEncoding(label); }
        catch (ArgumentException) { return null; }
    }
}
