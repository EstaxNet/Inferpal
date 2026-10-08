using System.Globalization;
using Inferpal.Localization;
using Inferpal.Services.Docs;
using Inferpal.Services.Rag;

namespace Inferpal.Services.Presentation;

/// <summary>The code index as the Code search page shows it — both editors render this, nothing recomputed.</summary>
/// <param name="State"><c>noWorkspace</c>, <c>indexing</c>, <c>failed</c>, <c>stopped</c>, <c>notBuilt</c> or <c>ready</c>.</param>
/// <param name="Notes">What the index left out or cannot search by meaning, one sentence each; empty on a complete index.</param>
/// <param name="OversizeNote">Files dropped for their size, counted; <paramref name="OversizeFiles"/> names them on demand.</param>
/// <param name="ModelLine">How the index searches: by meaning with which model, or keywords only and why.</param>
/// <param name="CanRebuild">A workspace is open and no pass is running.</param>
internal sealed record IndexCardModel(
    string State, string Title, string Detail, IReadOnlyList<string> Notes, string OversizeNote,
    IReadOnlyList<string> OversizeFiles, string ModelLine, string ButtonLabel, bool CanRebuild);

/// <summary>One @Docs site as the Code search page lists it.</summary>
/// <param name="State"><c>indexing</c>, <c>notIndexed</c>, <c>partial</c> (passages without search by meaning) or
/// <c>indexed</c> — the colour of its dot, never read back from its sentence.</param>
/// <param name="Address">The start URL without its scheme, as a person reads it.</param>
/// <param name="HoleNote">Why some of its passages are found by keywords only, with the remedy, or what its last crawl
/// said when it indexed nothing; empty otherwise.</param>
internal sealed record DocsSiteRow(
    string Id, string Title, string Address, string State, string Status, string HoleNote, bool Busy, string RemoveLabel);

/// <summary>One share of the conversation's window: instructions, tool descriptions or the conversation itself.</summary>
/// <param name="Key"><c>instructions</c>, <c>tools</c> or <c>conversation</c> — the colour of its bar.</param>
/// <param name="Percent">Its share of the window (of the total when no window is set), 0-100.</param>
internal sealed record ContextUsagePart(string Key, string Label, string Amount, double Percent);

/// <summary>How full the conversation's window is, and with what.</summary>
internal sealed record ContextUsageModel(string Summary, IReadOnlyList<ContextUsagePart> Parts);

/// <summary>One of the project's files the prompt reads, as the Context page lists it.</summary>
/// <param name="Exists">Only an existing file gets an Open link; the others say they are not created yet.</param>
internal sealed record ProjectFileRow(string Name, string Description, string FullPath, bool Exists);

/// <summary>One pinned file with what it costs the prompt.</summary>
/// <param name="Size">"0.9k tokens", or "not found" — a pin whose file is gone sends nothing.</param>
internal sealed record PinnedFileSize(string Path, string Size, bool Missing);

/// <summary>
/// The live blocks of the settings pages (<see cref="SettingSection.Widget"/>): what the product knows NOW, rather
/// than what the configuration says — the state of the index, the @Docs sites, how full the conversation is. Pure:
/// the two editors read the facts and render the result.
/// </summary>
internal static class SettingsWidgets
{
    /// <summary>The index card, from a snapshot of the index.</summary>
    /// <param name="ragEnabled">The background indexing setting: off, an index never built is a choice, not a fault.</param>
    /// <param name="configuredModel">The embedding model the settings name, for an index not built yet.</param>
    /// <param name="now">The local time, for "updated at": a time of day today, a date before.</param>
    public static IndexCardModel IndexCard(IndexSnapshot s, bool ragEnabled, string? configuredModel, DateTime now)
    {
        if (string.IsNullOrEmpty(s.RootDir))
            return new("noWorkspace", Strings.IndexCardNoWorkspace, Strings.IndexCardNoWorkspaceDetail, [],
                       string.Empty, [], string.Empty, Strings.IndexCardBuild, CanRebuild: false);

        var (state, title, detail) =
              s.IsIndexing         ? ("indexing", Strings.IndexCardIndexing,
                                      s.Total > 0 ? Strings.IndexCardProgress(N(s.Done), N(s.Total)) : string.Empty)
            : s.Failure is { } why ? ("failed",   Strings.IndexCardFailed, Strings.IndexCardFailedDetail(why.TrimEnd('.', ' ')))
            : s.Completed          ? ("ready",    Strings.IndexCardReady,  Strings.IndexCardFilesUpdated(N(s.Files), At(s.UpdatedAt, now)))
            : s.Stopped            ? ("stopped",  Strings.IndexCardStopped, Strings.IndexCardStoppedDetail)
            :                        ("notBuilt", Strings.IndexCardNotBuilt, ragEnabled ? string.Empty : Strings.IndexCardNotBuiltDetail);

        // What was left out speaks once a pass has listed the files; while one runs, the previous counts are stale.
        var notes = new List<string>();
        var oversizeNote = string.Empty;
        IReadOnlyList<string> oversizeFiles = [];
        if (state is "ready" or "stopped" or "failed")
        {
            if (s.SkippedFolder is { } gap) notes.Add(gap.Sentence());
            if (s.Model is not null && s.Unembedded > 0) notes.Add(Strings.IndexCardHoles(N(s.Unembedded), N(s.Chunks)));
            if (s.Oversize > 0)
            {
                oversizeNote  = Strings.IndexCardOversize(s.Oversize, CodeChunker.MaxFileSizeKilobytes);
                oversizeFiles = s.Oversize > s.OversizeFiles.Count
                    ? [.. s.OversizeFiles, Strings.IndexCardMoreFiles(s.Oversize - s.OversizeFiles.Count)]
                    : s.OversizeFiles;
            }
        }
        if (s.EmbeddingDown && ragEnabled) notes.Add(Strings.IndexCardEmbeddingDown);

        // The model the vectors came from; before a first pass, the one the settings name. Neither: nothing is
        // claimed about an index that does not exist yet.
        var model = s.Model ?? (state is "notBuilt" or "indexing" ? configuredModel : null);
        var modelLine = !ragEnabled          ? Strings.IndexCardModelOff
                      : model is not null    ? Strings.IndexCardModelSemantic(model)
                      : state is "notBuilt"  ? string.Empty
                      :                        Strings.IndexCardModelKeywords;

        return new(state, title, detail, notes, oversizeNote, oversizeFiles, modelLine,
                   state == "notBuilt" ? Strings.IndexCardBuild : Strings.IndexCardRebuild,
                   CanRebuild: !s.IsIndexing);
    }

    /// <summary>The @Docs sites: the configured ones, plus any the index still serves that the settings no longer list.</summary>
    /// <param name="reports">The last thing each crawl started from this page reported: a site that is still not indexed
    /// when its crawl ended shows it, since that sentence is the only one that says why (refused, nothing readable).</param>
    public static IReadOnlyList<DocsSiteRow> DocsSites(
        IReadOnlyList<DocSite> configured, IReadOnlyList<(DocSite Site, int PageCount, int ChunkCount)> indexed,
        IReadOnlyDictionary<string, int> unembedded, string? indexingId,
        IReadOnlyDictionary<string, string>? reports = null)
    {
        var stats = indexed.ToDictionary(x => x.Site.Id, x => x.PageCount, StringComparer.OrdinalIgnoreCase);
        var shown = configured.Concat(indexed.Select(x => x.Site).Where(s => configured.All(c => c.Id != s.Id)));
        return [.. shown.Select(site =>
        {
            var busy  = string.Equals(site.Id, indexingId, StringComparison.OrdinalIgnoreCase);
            var holes = unembedded.TryGetValue(site.Id, out var h) ? h : 0;
            var (state, status, note) =
                  busy                                  ? ("indexing", Strings.DocsSiteIndexing, string.Empty)
                : !stats.TryGetValue(site.Id, out var p) ? ("notIndexed", Strings.IndexCardNotBuilt,
                                                            reports?.TryGetValue(site.Id, out var r) == true ? r : string.Empty)
                : holes > 0                             ? ("partial", Strings.DocsSiteHoles(p, holes), Strings.DocsSiteHolesNote(holes))
                :                                         ("indexed", Strings.DocsSiteIndexed(p), string.Empty);
            return new DocsSiteRow(site.Id, site.Title, Address(site.StartUrl), state, status, note, busy,
                                   Strings.DocsRemoveSite(site.Title));
        })];
    }

    /// <summary>How full the conversation's window is, from the X-Ray panel's own counts.</summary>
    public static ContextUsageModel ContextUsage(XRayPanelModel xray)
    {
        var parts = new (string Key, string Label, int Tokens)[]
        {
            ("instructions", Strings.ContextUsageInstructions, xray.TotalTokens),
            ("tools",        Strings.ContextUsageTools,        xray.ToolTokens),
            ("conversation", Strings.ContextUsageConversation, xray.HistoryTokens),
        };
        var used  = parts.Sum(p => p.Tokens);
        var scale = xray.ContextWindow > 0 ? Math.Max(xray.ContextWindow, used) : Math.Max(used, 1);
        var summary = xray.ContextWindow > 0
            ? Strings.ContextUsageTokens(N(used), N(xray.ContextWindow))
            : Strings.PinnedFileTokens(N(used));
        return new(summary, [.. parts.Select(p => new ContextUsagePart(p.Key, p.Label, Amount(p.Tokens), 100.0 * p.Tokens / scale))]);
    }

    /// <summary>The project's files the prompt reads every question, whether they exist yet or not.</summary>
    public static IReadOnlyList<ProjectFileRow> ProjectFiles(string? root)
    {
        if (string.IsNullOrEmpty(root)) return [];
        var dir   = Path.Combine(root, ".inferpal");
        var rules = Path.Combine(dir, "rules");
        int ruleCount;
        try { ruleCount = Directory.Exists(rules) ? Directory.GetFiles(rules, "*.md").Length : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ruleCount = 0; }
        return
        [
            File(".inferpal/context.md", Strings.ProjectFileContext, Path.Combine(dir, "context.md")),
            File(".inferpal/memory.md",  Strings.ProjectFileMemory,  Path.Combine(dir, "memory.md")),
            File(".inferpal/notes.md",   Strings.ProjectFileNotes,   Path.Combine(dir, "notes.md")),
            new(".inferpal/rules/", Strings.ProjectFileRules(ruleCount), rules, Directory.Exists(rules)),
        ];

        static ProjectFileRow File(string name, string description, string path) =>
            new(name, description, path, System.IO.File.Exists(path));
    }

    /// <summary>What each pinned file costs the prompt, estimated like the X-Ray panel does.</summary>
    /// <param name="prompt">The X-Ray panel of the prompt as it is sent: a pinned file the prompt carries only part of is
    /// shown with the part that goes, not with its whole size.</param>
    /// <remarks>
    /// ⚠ The file sections share one budget (a character per token of the window): estimated on the whole file, a 120 KB
    /// log read "30.0k tokens" where each question carried about 2k — while the usage bar and the X-Ray on the same page
    /// measured the cut text.
    /// </remarks>
    public static IReadOnlyList<PinnedFileSize> PinnedSizes(IEnumerable<string> paths, XRayPanelModel? prompt = null) =>
        [.. paths.Select(path =>
        {
            try
            {
                if (!System.IO.File.Exists(path)) return new PinnedFileSize(path, Strings.PinnedFileMissing, Missing: true);
                var tokens = Commands.XRayCommandHandler.EstimateTokens(Tools.TextFileEncoding.ReadText(path));
                var sent   = prompt?.Sections.FirstOrDefault(s => s.Enabled && s.Id.StartsWith("Pinned|", StringComparison.Ordinal)
                                                                 && PathComparer.Default.Equals(s.Id["Pinned|".Length..], path));
                return new PinnedFileSize(path,
                    sent is not null && sent.Tokens < tokens
                        ? Strings.PinnedFileTokensSent(Amount(sent.Tokens), Amount(tokens))
                        : Strings.PinnedFileTokens(Amount(tokens)),
                    Missing: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new PinnedFileSize(path, Strings.PinnedFileMissing, Missing: true);
            }
        })];

    /// <summary>A token amount as the page shows it: exact below a thousand, "1.4k" above.</summary>
    internal static string Amount(int tokens) =>
        tokens < 1000 ? tokens.ToString("N0", CultureInfo.CurrentCulture)
                      : (tokens / 1000.0).ToString("0.0", CultureInfo.CurrentCulture) + "k";

    /// <summary>A start URL as a person reads it: no scheme, no trailing slash.</summary>
    internal static string Address(string url)
    {
        var shown = url;
        var scheme = shown.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) shown = shown[(scheme + 3)..];
        return shown.TrimEnd('/');
    }

    private static string N(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private static string At(DateTime? when, DateTime now) =>
        when is not { } t ? "—"
        : t.Date == now.Date ? t.ToString("t", CultureInfo.CurrentCulture)
        : t.ToString("g", CultureInfo.CurrentCulture);
}

/// <summary>
/// The Code search page's @Docs buttons — add, reindex, remove — through the <c>/docs</c> command itself, so the page and
/// the chat cannot disagree on what a gesture does (the same refusals, the same settings written, the same queue).
/// </summary>
internal sealed class SettingsDocsActions(Config.InferpalConfig config, DocsIndexService docs)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _reports =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Runs <c>/docs verb arg</c>; answers what the command answered (added, unknown id, usage, …).</summary>
    public Task<string> RunAsync(string verb, string arg, CancellationToken ct)
    {
        // The id is the one the command will give the site: a crawl reports without naming its site.
        var id = verb == "add" && DocSite.TryParse(config.DocSitesJson, out var sites, out _) && DocSite.IsValidHttpUrl(arg)
            ? DocSite.CreateAmong(arg, null, sites).Id
            : arg;
        _reports.TryRemove(id, out _);
        return Commands.DocsCommandHandler.HandleAsync(
            config, docs, ["/docs", verb, arg], new Recorder(m => _reports[id] = m), ct);
    }

    /// <summary>The sites as the page lists them.</summary>
    public async Task<IReadOnlyList<DocsSiteRow>> RowsAsync(CancellationToken ct)
    {
        DocSite.TryParse(config.DocSitesJson, out var configured, out _);
        return SettingsWidgets.DocsSites(configured, await docs.SitesAsync(ct), docs.UnembeddedBySite,
                                         docs.IndexingSiteId, _reports);
    }

    /// <summary>A progress sink that records on the reporting thread: <see cref="Progress{T}"/> would post to a
    /// context that may never run it.</summary>
    private sealed class Recorder(Action<string> record) : IProgress<string>
    {
        public void Report(string value) => record(value);
    }
}
