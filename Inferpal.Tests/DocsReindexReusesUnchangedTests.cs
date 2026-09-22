using System.IO;
using System.Linq;
using Inferpal.Config;
using Inferpal.Services.Docs;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Re-indexing a documentation source embeds what CHANGED, not the whole site.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <c>DocChunk.ContentHash</c> was written and persisted and <b>never compared</b>, so every
/// <c>/docs reindex</c> re-embedded every passage of the site. That is not merely slow: embedding is
/// the step that trips the circuit breaker, and an open circuit is what leaves chunks stored
/// <b>without a vector</b> — the hole this corpus keeps permanently.
/// </para>
/// <para>
/// ⭐ And <c>/docs reindex</c> is the <b>only remedy the product names</b> for that hole. So the
/// remedy was the operation most likely to re-open the very breaker that caused it, and its cost
/// scaled with the whole corpus instead of with the damage.
/// </para>
/// <para>
/// ⚠ Reuse is only safe because the embedding model is now recorded beside the vectors: a vector may
/// be reused when the passage is unchanged <b>and</b> it came from the model in use. Both halves are
/// pinned below, because reusing across a model change would serve noise under a ✅.
/// </para>
/// <para>
/// ⚠ The arm that matters most is the third: a chunk left <b>without a vector</b> must be embedded
/// again, or the fix would stop <c>/docs reindex</c> from repairing the hole — turning an
/// optimisation into a regression of the one remedy that exists.
/// </para>
/// </remarks>
[Collection(DocsDatabaseCollection.Name)]
public sealed class DocsReindexReusesUnchangedTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"docsreuse-{Guid.NewGuid():N}");

    public DocsReindexReusesUnchangedTests() => DocsDatabase.OverrideDirForTests = _dir;

    public void Dispose()
    {
        DocsDatabase.OverrideDirForTests = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static readonly DocSite Site = DocSite.Create("https://docs.example.com/", "Example");

    private static string Body(string marker) => string.Join(" ",
        Enumerable.Range(0, 200).Select(i => i % 20 == 0 ? marker : $"word{i % 11}"));

    /// <summary>Counts what the pass actually asked the backend to embed.</summary>
    private sealed class Counting
    {
        public int Calls;
        public required FakeInferenceProvider Client { get; init; }
    }

    private static Counting Provider(int embedFirst = int.MaxValue)
    {
        var client = new FakeInferenceProvider();
        var c = new Counting { Client = client };
        client.OnEmbedding = _ => ++c.Calls <= embedFirst ? [1f, 0f] : null;
        return c;
    }

    private static DocsIndexService Service(Counting client, string model, Func<List<DocCrawler.Page>> pages) =>
        new(client.Client, new InferpalConfig { RagEmbeddingModel = model })
        {
            CrawlForTests = (_, _) => Task.FromResult(pages()),
        };

    private static List<DocCrawler.Page> OnePage(string text) =>
        [new("https://docs.example.com/a", "Alpha", text)];

    [Fact]
    public async Task ReindexingAnUnchangedSite_EmbedsNothing()
    {
        var client = Provider();
        var docs   = Service(client, "model-a", () => OnePage(Body("alpha")));

        await docs.AddOrReindexAsync(Site, progress: null, CancellationToken.None);

        // WITNESS: the first pass really embedded, so "0 on the second" is a reuse and not an index
        // that never had anything to reuse.
        var first = client.Calls;
        Assert.True(first > 0, "the first pass embedded nothing: this test would measure nothing.");
        Assert.True(docs.ChunkCount > 0, "nothing was indexed.");

        await docs.AddOrReindexAsync(Site, progress: null, CancellationToken.None);

        Assert.Equal(first, client.Calls);                 // not one more call
        Assert.Equal(0, docs.UnembeddedCount);             // and every chunk still carries its vector
    }

    [Fact]
    public async Task APageWhoseTextChanged_IsReEmbedded()
    {
        // REFERENCE ARM: without it, "reuse everything" passes the test above and serves vectors
        // that no longer describe the page — a far worse defect than the slow re-index.
        var client = Provider();
        var text   = Body("alpha");
        var docs   = Service(client, "model-a", () => OnePage(text));

        await docs.AddOrReindexAsync(Site, progress: null, CancellationToken.None);
        var first = client.Calls;

        text = Body("something else entirely");
        await docs.AddOrReindexAsync(Site, progress: null, CancellationToken.None);

        Assert.True(client.Calls > first,
            "the page changed and nothing was re-embedded: the index now describes text that is gone.");
    }

    [Fact]
    public async Task AChunkLeftWithoutAVector_IsReEmbedded_BecauseThatIsWhatReindexRepairs()
    {
        // REFERENCE ARM, the one that matters: /docs reindex exists to fill the hole. A reuse that
        // skipped un-embedded chunks would leave the hole permanent and remove its only remedy.
        var client = Provider(embedFirst: 1);              // the circuit "opens" after one chunk
        var text   = string.Join("\n", Enumerable.Range(0, 40).Select(i => Body($"m{i}")));
        var docs   = Service(client, "model-a", () => OnePage(text));

        await docs.AddOrReindexAsync(Site, progress: null, CancellationToken.None);

        // WITNESS: the corpus really is holed.
        Assert.True(docs.UnembeddedCount > 0, "no hole was created: this test would measure nothing.");

        client.Client.OnEmbedding = _ => { client.Calls++; return [1f, 0f]; };   // the backend is back
        await docs.AddOrReindexAsync(Site, progress: null, CancellationToken.None);

        Assert.Equal(0, docs.UnembeddedCount);
    }

    [Fact]
    public async Task AReindexDuringAnOutage_KeepsTheVectorsItAlreadyHad()
    {
        // ⚠ The pass REPLACES the site's stored chunks. Abandoning the embedding loop when the
        // breaker opens therefore persisted every later chunk WITHOUT the vector it already had:
        // a re-index attempted during an outage widened the hole it exists to close. Reuse first,
        // and skip only the calls, so it can narrow the hole and never widen it.
        // ⚠ The decor has to put the UNREUSABLE chunk FIRST, or the loop never reaches the breaker
        // at all and the test measures nothing — the first version of it passed under `break` and
        // under `continue` alike. So: the earlier pass left chunk 0 without a vector and embedded
        // the rest, which is a corpus holed at the front, re-indexed while the backend is still down.
        var client = Provider();
        var first  = true;
        client.Client.OnEmbedding = _ =>
        {
            client.Calls++;
            if (!first) return [1f, 0f];
            first = false;
            return null;                                       // chunk 0 gets no vector
        };

        var text = string.Join("\n", Enumerable.Range(0, 40).Select(i => Body($"m{i}")));
        var docs = Service(client, "model-a", () => OnePage(text));

        await docs.AddOrReindexAsync(Site, progress: null, CancellationToken.None);

        // WITNESS: exactly one chunk is holed, and there are plenty behind it to lose.
        Assert.Equal(1, docs.UnembeddedCount);
        Assert.True(docs.ChunkCount > 3, "too few chunks behind the hole to measure anything.");
        var total = docs.ChunkCount;

        client.Client.OnEmbedding            = _ => { client.Calls++; return null; };
        client.Client.IsEmbeddingCircuitOpen = true;           // the backend went away
        await docs.AddOrReindexAsync(Site, progress: null, CancellationToken.None);

        Assert.Equal(total, docs.ChunkCount);
        Assert.Equal(1, docs.UnembeddedCount);                 // still one, never all of them
    }

    [Fact]
    public async Task AfterTheEmbeddingModelChanges_EverythingIsReEmbedded()
    {
        // REFERENCE ARM: reuse must never win over the model guard — vectors from another model are
        // noise, and serving them under a ✅ is the defect that guard exists for.
        var client = Provider();
        var docs   = Service(client, "model-a", () => OnePage(Body("alpha")));

        await docs.AddOrReindexAsync(Site, progress: null, CancellationToken.None);
        var first = client.Calls;

        var moved = Service(client, "model-b", () => OnePage(Body("alpha")));
        await moved.LoadAsync(CancellationToken.None);
        await moved.AddOrReindexAsync(Site, progress: null, CancellationToken.None);

        Assert.Equal(first * 2, client.Calls);
        Assert.Equal(0, moved.UnembeddedCount);
    }
}
