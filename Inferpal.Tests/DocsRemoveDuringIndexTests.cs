using System.IO;
using Inferpal.Config;
using Inferpal.Services.Commands;
using Inferpal.Services.Docs;
using Xunit;

namespace Inferpal.Tests;

/// <summary>The tests that repoint <see cref="DocsDatabase.OverrideDirForTests"/>, which is static.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DocsDatabaseCollection
{
    public const string Name = "DocsDatabase";
}

/// <summary>
/// <c>/docs add</c> starts a detached indexing pass that only writes the source at the end, after
/// minutes of crawling and embedding. Removing the source meanwhile removed nothing: the pass wrote it
/// afterwards, <c>@Docs</c> served it, and <c>/docs remove</c> called it unknown.
/// </summary>
[Collection(DocsDatabaseCollection.Name)]
public class DocsRemoveDuringIndexTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"docs-{Guid.NewGuid():N}");

    public DocsRemoveDuringIndexTests() => DocsDatabase.OverrideDirForTests = _dir;

    public void Dispose()
    {
        DocsDatabase.OverrideDirForTests = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>A service whose crawl waits for <paramref name="gate"/> before returning a page.</summary>
    private static DocsIndexService Service(TaskCompletionSource gate) =>
        new(new FakeInferenceProvider(), new InferpalConfig())
        {
            CrawlForTests = async (_, ct) =>
            {
                await gate.Task.WaitAsync(ct);
                return [new DocCrawler.Page("https://docs.example.com/a", "A", "Some documentation text.")];
            },
        };

    [Fact]
    public async Task ASourceRemovedWhileItIsIndexed_DoesNotComeBack()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var docs = Service(gate);
        var site = DocSite.Create("https://docs.example.com/", "Example");

        var pass = docs.AddOrReindexAsync(site, progress: null, CancellationToken.None);   // blocked in the crawl
        await docs.RemoveAsync(site.Id, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        gate.TrySetResult();
        await pass.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.DoesNotContain(await docs.SitesAsync(), s => s.Site.Id == site.Id);
        Assert.DoesNotContain(await new DocsDatabase().LoadSitesAsync(CancellationToken.None), s => s.Site.Id == site.Id);
    }

    /// <summary>Witness: without a removal the same pass does save the source — the harness indexes.</summary>
    [Fact]
    public async Task ASourceNobodyRemoves_IsSaved()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var docs = Service(gate);
        var site = DocSite.Create("https://docs.example.com/", "Example");

        var pass = docs.AddOrReindexAsync(site, progress: null, CancellationToken.None);
        gate.TrySetResult();
        await pass.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains(await docs.SitesAsync(), s => s.Site.Id == site.Id);
    }

    /// <summary>
    /// An orphan left by that race before the fix — served by the index, absent from the settings — is
    /// shown by <c>/docs</c> and removed by <c>/docs remove</c>: both only read the settings.
    /// </summary>
    [Fact]
    public async Task AnOrphanLeftInTheIndex_IsListedAndCanBeRemoved()
    {
        var orphan = DocSite.Create("https://docs.example.com/", "Example");
        await new DocsDatabase().SaveSiteAsync(orphan, 1, [], CancellationToken.None);
        var docs = new DocsIndexService(new FakeInferenceProvider(), new InferpalConfig());
        await docs.LoadAsync(CancellationToken.None);
        var config = new InferpalConfig { DocSitesJson = "" };

        var listing = await DocsCommandHandler.HandleAsync(
            config, docs, ["/docs"], new Progress<string>(), CancellationToken.None);
        Assert.Contains(orphan.Id, listing);

        await DocsCommandHandler.HandleAsync(
            config, docs, ["/docs", "remove", orphan.Id], new Progress<string>(), CancellationToken.None);
        Assert.Empty(await docs.SitesAsync());
        Assert.Empty(await new DocsDatabase().LoadSitesAsync(CancellationToken.None));
    }

    /// <summary>
    /// A bare <c>/docs reindex</c> indexes a list taken when it starts: a source removed before the loop
    /// reached it was indexed and written back — removal only stops the pass that is running.
    /// </summary>
    [Fact]
    public async Task ASourceRemovedBeforeAReindexReachesIt_IsSkipped()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var docs = new DocsIndexService(new FakeInferenceProvider(), new InferpalConfig())
        {
            CrawlForTests = async (url, ct) =>
            {
                if (url.Contains("first", StringComparison.Ordinal)) await gate.Task.WaitAsync(ct);
                return [new DocCrawler.Page(url + "a", "A", "Some documentation text.")];
            },
        };
        var first   = DocSite.Create("https://first.example.com/", "First");
        var second  = DocSite.Create("https://second.example.com/", "Second");
        var removed = new HashSet<string>();

        var reindex = docs.ReindexAsync([first, second], s => !removed.Contains(s.Id), progress: null);
        removed.Add(second.Id);   // removed while the first one is being indexed
        await docs.RemoveAsync(second.Id, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        gate.TrySetResult();
        await reindex.WaitAsync(TimeSpan.FromSeconds(30));

        var ids = (await docs.SitesAsync()).Select(s => s.Site.Id).ToList();
        Assert.Contains(first.Id, ids);   // witness: the loop did index
        Assert.DoesNotContain(second.Id, ids);
    }

    /// <summary>
    /// A source added while another pass runs is indexed once that pass ends. It was dropped with a
    /// progress line, after <c>/docs add</c> had announced it and saved it to the settings: it stayed at
    /// 0 pages until a manual reindex — and a <c>/docs reindex</c> started during an add skipped every source.
    /// </summary>
    [Fact]
    public async Task ASourceAddedDuringAnotherPass_IsIndexedOnceThatPassEnds()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var docs = GatedOnFirst(gate);

        var running = docs.AddOrReindexAsync(First, progress: null, CancellationToken.None);   // blocked in the crawl
        var queued  = docs.AddOrReindexAsync(Second, progress: null, CancellationToken.None);
        gate.TrySetResult();
        await Task.WhenAll(running, queued).WaitAsync(TimeSpan.FromSeconds(30));

        var ids = (await docs.SitesAsync()).Select(s => s.Site.Id).ToList();
        Assert.Contains(First.Id, ids);   // witness: the blocked pass did index
        Assert.Contains(Second.Id, ids);
    }

    /// <summary>
    /// A source waiting for its turn and removed meanwhile is not written when its turn comes: the removal
    /// stops only the pass that is running, so the waiting one asks again whether it is still wanted.
    /// </summary>
    [Fact]
    public async Task AQueuedSourceRemovedBeforeItsTurn_IsNotWritten()
    {
        var gate    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var docs    = GatedOnFirst(gate);
        var removed = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();

        var running = docs.AddOrReindexAsync(First, progress: null, CancellationToken.None);
        var queued  = docs.AddOrReindexAsync(Second, progress: null, CancellationToken.None, s => !removed.ContainsKey(s.Id));
        removed[Second.Id] = true;
        await docs.RemoveAsync(Second.Id, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        gate.TrySetResult();
        await Task.WhenAll(running, queued).WaitAsync(TimeSpan.FromSeconds(30));

        var ids = (await docs.SitesAsync()).Select(s => s.Site.Id).ToList();
        Assert.Contains(First.Id, ids);   // witness: the blocked pass did index
        Assert.DoesNotContain(Second.Id, ids);
    }

    /// <summary>
    /// <c>/docs add</c> hands its pass the "is this source still configured?" check: the pass may wait behind
    /// another one, and a source removed meanwhile would otherwise be written back.
    /// </summary>
    [Fact]
    public void DocsAdd_GivesItsPassTheStillConfiguredCheck()
    {
        var handler = ConventionCoverageTests.CodeOnly(
            Path.Combine(RepoRoot(), "Inferpal.Core", "Services", "Commands", "DocsCommandHandler.cs"));
        var at = handler.IndexOf("docs.AddOrReindexAsync(", StringComparison.Ordinal);
        Assert.True(at >= 0, "the /docs add call moved — the rule measures nothing.");

        var call = handler[at..handler.IndexOf(';', at)];
        Assert.Contains("StillConfigured(", call, StringComparison.Ordinal);
    }

    private static readonly DocSite First  = DocSite.Create("https://first.example.com/", "First");
    private static readonly DocSite Second = DocSite.Create("https://second.example.com/", "Second");

    /// <summary>A service whose crawl of <see cref="First"/> waits for <paramref name="gate"/>.</summary>
    private static DocsIndexService GatedOnFirst(TaskCompletionSource gate) =>
        new(new FakeInferenceProvider(), new InferpalConfig())
        {
            CrawlForTests = async (url, ct) =>
            {
                if (url.Contains("first", StringComparison.Ordinal)) await gate.Task.WaitAsync(ct);
                return [new DocCrawler.Page(url + "a", "A", "Some documentation text.")];
            },
        };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
