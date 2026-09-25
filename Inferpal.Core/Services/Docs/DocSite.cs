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

        var label = Unquoted(title);
        if (string.IsNullOrWhiteSpace(label)) label = host;
        return new DocSite(Slugify(label), label, startUrl.Trim());
    }

    /// <summary>
    /// The title as the user meant it. <c>/docs add &lt;url&gt; "React Router"</c> is the documented way to give a
    /// title of several words, and the command line keeps the quotes: without this the source is named
    /// <c>"React Router"</c>, quotes included, everywhere it is shown.
    /// </summary>
    private static string Unquoted(string? title)
    {
        var t = title?.Trim() ?? string.Empty;
        if (t.Length >= 2 && ((t[0] == '"' && t[^1] == '"') || (t[0] == '\'' && t[^1] == '\'') || (t[0] == '“' && t[^1] == '”')))
            t = t[1..^1].Trim();
        return t;
    }

    /// <summary>
    /// <see cref="Create"/>, with an id that does not silently take over another source.
    /// </summary>
    /// <remarks>
    /// The id is derived, not typed: from the host when there is no title, and <see cref="FallbackId"/>
    /// when a title has no Latin letter to slug (Russian, Japanese, Korean, Chinese — languages the
    /// product ships). <see cref="Upsert"/> replaces the source carrying the same id and indexing
    /// rewrites its chunks, so a second documentation on the same host, or a second non-Latin title,
    /// deleted the first — configuration and index. A derived id is therefore made free (-2, -3…)
    /// unless the source already under it is this very URL, which re-adding refreshes. An explicit
    /// Latin title still names the source to update.
    /// </remarks>
    public static DocSite CreateAmong(string startUrl, string? title, IReadOnlyList<DocSite> existing)
    {
        var site = Create(startUrl, title);
        if (!string.IsNullOrWhiteSpace(title) && site.Id != FallbackId) return site;

        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? site.Id : $"{site.Id}-{n}";
            var holder    = existing.FirstOrDefault(s => s.Id == candidate);
            if (holder is null || SameUrl(holder.StartUrl, site.StartUrl))
                return site with { Id = candidate };
        }
    }

    /// <summary>Id a label with nothing sluggable gets.</summary>
    private const string FallbackId = "doc";

    private static bool SameUrl(string a, string b) =>
        string.Equals(a.Trim().TrimEnd('/'), b.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses the persisted JSON array; tolerant of malformed input (returns empty list).</summary>
    public static List<DocSite> Parse(string? json) => TryParse(json, out var sites, out _) ? sites : [];

    /// <summary>
    /// Parses the persisted JSON array. <c>false</c> when it cannot be read, with the reason in
    /// <paramref name="problem"/>: an unreadable list is not an empty one, and writing a new list over
    /// it would erase every source it still holds.
    /// </summary>
    public static bool TryParse(string? json, out List<DocSite> sites, out string? problem)
    {
        sites   = [];
        problem = null;
        if (string.IsNullOrWhiteSpace(json)) return true;
        try
        {
            var parsed = JsonSerializer.Deserialize<List<DocSite?>>(json) ?? [];
            var broken = parsed.FindIndex(s =>
                s is null || string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.StartUrl));
            if (broken >= 0)
            {
                problem = $"entry {broken + 1} has no \"id\" or \"startUrl\"";
                return false;
            }
            sites = [.. parsed.Select(s => s!)];
            return true;
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            return false;
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
        IReadOnlyDictionary<string, (int Pages, int Chunks)> stats,
        IReadOnlyDictionary<string, int>? unembeddedBySite = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Strings.DocsListHeader);
        foreach (var s in sites)
        {
            var (pc, cc) = stats.TryGetValue(s.Id, out var st) ? st : (0, 0);
            var hole = unembeddedBySite is not null && unembeddedBySite.TryGetValue(s.Id, out var missing)
                ? Docs.DocsIndexService.HoleNote(missing, cc, s.Id)
                : string.Empty;
            sb.AppendLine($"- **{s.Id}** — {s.Title} ({pc} pages, {cc} chunks) · {s.StartUrl}{hole}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Converts a label into a short, unique-enough kebab-case id.</summary>
    private static string Slugify(string text)
    {
        // The label can be a crawled page title: same untrusted class as the rest of Services/Docs,
        // same budget. The pattern is linear, but the rule is the guard rail, not the pattern.
        var slug = Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9]+", "-",
                                 RegexOptions.None, RegexBudget.Default)
                        .Trim('-');
        if (slug.Length > 32) slug = slug[..32].TrimEnd('-');
        return string.IsNullOrEmpty(slug) ? FallbackId : slug;
    }
}
