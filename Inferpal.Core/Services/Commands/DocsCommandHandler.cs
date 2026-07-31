using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Docs;

namespace Inferpal.Services.Commands;

/// <summary>
/// Execution logic for <c>/docs add|remove|reindex|list</c> — the external documentation sources
/// backing <c>search_docs</c>. Everything it touches (config, <see cref="DocSite"/>,
/// <see cref="DocsIndexService"/>) lives in the Core, so the whole command is shared; the front-ends
/// only decide where the progress messages of the background crawl are displayed.
/// </summary>
internal static class DocsCommandHandler
{
    /// <param name="config">Config holding <c>DocSitesJson</c>; persisted here on mutation.</param>
    /// <param name="docs">Documentation index (crawl + embed).</param>
    /// <param name="parts">Tokenised command; <c>parts[1]</c> is the sub-command (default <c>list</c>).</param>
    /// <param name="progress">Sink for crawl progress: chat bubbles in VS, <c>chat/step</c>
    /// notifications in the headless host.</param>
    public static async Task<string> HandleAsync(
        InferpalConfig    config,
        DocsIndexService  docs,
        string[]          parts,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var sub   = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "list";
        var sites = DocSite.Parse(config.DocSitesJson);

        switch (sub)
        {
            case "add":
            {
                if (parts.Length < 3 || !DocSite.IsValidHttpUrl(parts[2])) return Strings.DocsUsage;

                var title = parts.Length > 3 ? string.Join(" ", parts[3..]) : null;
                var site  = DocSite.Create(parts[2], title);

                config.DocSitesJson = DocSite.Serialize(DocSite.Upsert(sites, site));
                config.Save();

                // Crawling is long: it runs detached, reporting through `progress`. Deliberately
                // not tied to `ct` — cancelling the command must not kill an ongoing crawl.
                _ = Task.Run(() => docs.AddOrReindexAsync(site, progress, CancellationToken.None), CancellationToken.None);
                return Strings.DocsAdded(site.Title);
            }

            case "remove":
            {
                if (parts.Length < 3) return Strings.DocsUsage;

                var id      = parts[2].ToLowerInvariant();
                var updated = DocSite.Remove(sites, id);
                if (updated is null) return Strings.DocsNoSites;

                config.DocSitesJson = DocSite.Serialize(updated);
                config.Save();
                await docs.RemoveAsync(id, ct);
                return Strings.DocsRemoved(id);
            }

            case "reindex":
            {
                var target = parts.Length >= 3
                    ? sites.FirstOrDefault(x => x.Id == parts[2].ToLowerInvariant())
                    : null;
                var toIndex = target is not null ? [target] : sites.ToArray();
                if (toIndex.Length == 0) return Strings.DocsNoSites;

                _ = Task.Run(async () =>
                {
                    foreach (var site in toIndex)
                        await docs.AddOrReindexAsync(site, progress, CancellationToken.None);
                }, CancellationToken.None);
                return Strings.DocsReindexing(target?.Title ?? $"{toIndex.Length}");
            }

            default:
            {
                if (sites.Count == 0) return Strings.DocsNoSites;

                var stats = docs.Sites.ToDictionary(x => x.Site.Id, x => (x.PageCount, x.ChunkCount));
                return DocSite.FormatList(sites, stats);
            }
        }
    }
}
