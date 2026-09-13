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
}
