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
        var sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "list";

        // Every sub-command stops here: `add` and `remove` would write a new list over the one that
        // could not be read, erasing the sources it still holds.
        if (!DocSite.TryParse(config.DocSitesJson, out var sites, out var problem))
            return Strings.DocsSourcesUnreadable(problem ?? string.Empty);

        switch (sub)
        {
            case "add":
            {
                if (parts.Length < 3 || !DocSite.IsValidHttpUrl(parts[2])) return Strings.DocsUsage;

                var title = parts.Length > 3 ? string.Join(" ", parts[3..]) : null;
                var site  = DocSite.CreateAmong(parts[2], title, sites);

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
                if (updated is null)
                {
                    // A source the index still serves but the settings no longer list — written by an
                    // indexing pass that outlived its removal — leaves the index here, or nothing ever could.
                    if ((await docs.SitesAsync(ct)).Any(x => x.Site.Id == id))
                    {
                        await docs.RemoveAsync(id, ct);
                        return Strings.DocsRemoved(id);
                    }
                    // "No documentation indexed yet" is only true when there is none: with sources present, a
                    // mistyped id read as every documentation being gone.
                    return sites.Count == 0 ? Strings.DocsNoSites : Strings.DocsUnknownId(id);
                }

                config.DocSitesJson = DocSite.Serialize(updated);
                config.Save();
                await docs.RemoveAsync(id, ct);
                return Strings.DocsRemoved(id);
            }

            case "reindex":
            {
                if (sites.Count == 0) return Strings.DocsNoSites;

                // A mistyped id must not become "reindex everything": the fallback crawled and embedded
                // every source, announced as a success. Only a bare `/docs reindex` means all of them.
                DocSite? target = null;
                if (parts.Length >= 3)
                {
                    var id = parts[2].ToLowerInvariant();
                    target = sites.FirstOrDefault(x => x.Id == id);
                    if (target is null) return Strings.DocsUnknownId(id);
                }
                var toIndex = target is not null ? [target] : sites.ToArray();

                _ = Task.Run(async () =>
                {
                    foreach (var site in toIndex)
                        await docs.AddOrReindexAsync(site, progress, CancellationToken.None);
                }, CancellationToken.None);
                return Strings.DocsReindexing(target?.Title ?? $"{toIndex.Length}");
            }

            default:
            {
                // The index can hold a source the settings no longer list; it is still served, so it is shown.
                var indexed = await docs.SitesAsync(ct);
                var shown   = sites.Concat(indexed.Select(x => x.Site).Where(s => sites.All(c => c.Id != s.Id))).ToList();
                if (shown.Count == 0) return Strings.DocsNoSites;

                var stats = indexed.ToDictionary(x => x.Site.Id, x => (x.PageCount, x.ChunkCount));
                return DocSite.FormatList(shown, stats);
            }
        }
    }
}
