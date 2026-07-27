using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Inferpal.Localization;

namespace Inferpal.Services.Docs;

/// <summary>
/// A user-added external documentation source to be crawled and indexed for the
/// <c>search_docs</c> tool. Persisted as a JSON array in <c>config.DocSitesJson</c>.
/// </summary>
/// <param name="Id">Short kebab-case identifier (referenced by <c>/docs remove</c>).</param>
/// <param name="Title">Human-readable label shown in <c>/docs list</c> and search results.</param>
/// <param name="StartUrl">Crawl entry point; only same-domain pages under its path prefix are followed.</param>
internal sealed record DocSite(
    [property: JsonPropertyName("id")]       string Id,
    [property: JsonPropertyName("title")]    string Title,
    [property: JsonPropertyName("startUrl")] string StartUrl)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>
    /// Builds a <see cref="DocSite"/> from a start URL and an optional title.
    /// When the title is empty, the URL host is used. The id is derived from the title.
    /// </summary>
    public static DocSite Create(string startUrl, string? title)
    {
        var host = Uri.TryCreate(startUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : startUrl;

        var label = string.IsNullOrWhiteSpace(title) ? host : title.Trim();
        return new DocSite(Slugify(label), label, startUrl.Trim());
    }

    /// <summary>Parses the persisted JSON array; tolerant of malformed input (returns empty list).</summary>
    public static List<DocSite> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<DocSite>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Serialises a list of sites for storage in <c>config.DocSitesJson</c>.</summary>
    public static string Serialize(IReadOnlyList<DocSite> sites) =>
        JsonSerializer.Serialize(sites, JsonOpts);

    /// <summary>True when the string is an absolute http(s) URL — the only kind <c>/docs add</c> accepts.</summary>
    public static bool IsValidHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Adds <paramref name="site"/>, replacing any existing source with the same id.</summary>
    public static List<DocSite> Upsert(IReadOnlyList<DocSite> sites, DocSite site) =>
        sites.Where(s => s.Id != site.Id).Append(site).ToList();

    /// <summary>Removes the site with <paramref name="id"/>; null when no such site exists.</summary>
    public static List<DocSite>? Remove(IReadOnlyList<DocSite> sites, string id) =>
        sites.All(s => s.Id != id) ? null : sites.Where(s => s.Id != id).ToList();

    /// <summary>The <c>/docs</c> listing: one line per source with its crawl stats.</summary>
    public static string FormatList(
        IReadOnlyList<DocSite> sites,
        IReadOnlyDictionary<string, (int Pages, int Chunks)> stats)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Strings.DocsListHeader);
        foreach (var s in sites)
        {
            var (pc, cc) = stats.TryGetValue(s.Id, out var st) ? st : (0, 0);
            sb.AppendLine($"- **{s.Id}** — {s.Title} ({pc} pages, {cc} chunks) · {s.StartUrl}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Converts a label into a short, unique-enough kebab-case id.</summary>
    private static string Slugify(string text)
    {
        var slug = Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9]+", "-")
                        .Trim('-');
        if (slug.Length > 32) slug = slug[..32].TrimEnd('-');
        return string.IsNullOrEmpty(slug) ? "doc" : slug;
    }
}
