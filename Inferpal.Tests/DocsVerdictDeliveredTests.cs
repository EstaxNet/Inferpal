using System.IO;
using Inferpal.Config;
using Inferpal.Services.Docs;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ A documentation crawl's verdict reaches VS Code and the ACP clients: it is a result, not progress.
/// </summary>
/// <remarks>
/// <c>/docs add</c> answers "added" and crawls in the background. Its verdict — the pages and chunks, or why there are
/// none (a private address refused, the site's refusals, a name that does not resolve, the page cap) — went out as
/// progress (<c>chat/step</c>), which VS Code shows only during a turn and an ACP client not at all: the source stayed
/// at 0 pages with no reason anywhere. Visual Studio shows each message as a bubble. The pass now raises its verdict once,
/// and the host forwards it as a lasting notice.
/// </remarks>
[Collection(DocsDatabaseCollection.Name)]
public sealed class DocsVerdictDeliveredTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"docsverdict-{Guid.NewGuid():N}");

    public DocsVerdictDeliveredTests() => DocsDatabase.OverrideDirForTests = _dir;

    public void Dispose()
    {
        DocsDatabase.OverrideDirForTests = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static DocSite Site => DocSite.Create("https://docs.example.com/", "Example");

    private static async Task<List<string>> VerdictsOfAsync(List<DocCrawler.Page> pages)
    {
        var docs = new DocsIndexService(new FakeInferenceProvider(), new InferpalConfig())
        {
            CrawlForTests = (_, _) => Task.FromResult(pages),
        };
        var verdicts = new List<string>();
        docs.PassFinished += verdicts.Add;
        await docs.AddOrReindexAsync(Site, progress: null, CancellationToken.None);
        Assert.Equal(docs.Status, Assert.Single(verdicts));   // once, and it is the pass's verdict
        return verdicts;
    }

    [Fact]
    public async Task ACrawlThatFoundNothing_RaisesWhy()
    {
        var verdict = Assert.Single(await VerdictsOfAsync([]));
        Assert.Contains("no readable pages found at https://docs.example.com/", verdict, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACrawlThatIndexed_RaisesItsCounts()
    {
        var verdict = Assert.Single(await VerdictsOfAsync(
            [new("https://docs.example.com/a", "Retries", "Retry the request after a delay that doubles each time.")]));
        Assert.Contains("✅ Example", verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHost_ForwardsTheVerdict_AsALastingNotice()
    {
        var host = ConventionCoverageTests.CodeOnly(Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal.Host", "HostServer.cs"));
        Assert.Contains("docs.PassFinished += verdict => Notify(\"host/notice\", new { text = verdict, result = true });", host, StringComparison.Ordinal);
        // WITNESS: a host notice is drawn out of a turn in VS Code.
        Assert.Contains("onNotice: (text) => this.sayOutOfTurn(text),", WebviewRebuildTests.TsCode("chatViewProvider.ts"), StringComparison.Ordinal);
    }
}
