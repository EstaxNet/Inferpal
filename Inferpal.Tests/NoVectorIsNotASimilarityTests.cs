using System.IO;
using System.Linq;
using Inferpal.Config;
using Inferpal.Services.Docs;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A chunk held <b>without a vector</b> never comes back as a semantic hit — on either index.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Both hybrid searches score the vector side as
/// <c>c.Embedding is { Length: &gt; 0 } ? Cosine(…) : 0f</c> and then keep what clears
/// <c>RagSimilarityThreshold</c>. That <c>0f</c> is a <b>sentinel for "there is no vector"</b>, not a
/// similarity — and the threshold is a plain setting the user types, offered over 0–1 with no clamp.
/// At <b>0</b> the sentinel clears the gate: a chunk that was never embedded is ranked by the vector
/// side and reported as a <b>cosine of 0.00</b>.
/// </para>
/// <para>
/// ⭐ It is the mirror of the defect the provenance work closed — there, a purely lexical hit scored
/// <c>0f</c> and passed for a similarity of zero; here a chunk with no vector at all does. The fix
/// wrote <c>IsCosine</c> so provenance would live in the data rather than be inferred from a number,
/// and then the vector side went on <b>manufacturing</b> the number it inferred from.
/// </para>
/// <para>
/// ⚠ Holding chunks without vectors is not an edge case, it is designed behaviour on both indexes
/// (<c>chunk.Embedding = emb; // null if model unavailable</c>, "index continues without embeddings")
/// and on <c>@Docs</c> it is documented as <b>permanent</b>. And 0 is exactly the threshold someone
/// getting no results would reach for.
/// </para>
/// <para>
/// ⚠ The discriminator is <b>having a vector</b>, never <b>scoring above zero</b>: a real embedding
/// orthogonal to the query has a cosine of exactly 0 and is a legitimate semantic hit. Both halves
/// of each pair below exist so the cheap fix — drop anything scoring 0 — fails the reference arm.
/// </para>
/// </remarks>
[Collection(DocsDatabaseCollection.Name)]
public sealed class NoVectorIsNotASimilarityTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"novector-{Guid.NewGuid():N}");

    private readonly List<IDisposable> _services = [];

    public NoVectorIsNotASimilarityTests()
    {
        DocsDatabase.OverrideDirForTests = _dir;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        foreach (var s in _services) { try { s.Dispose(); } catch { } }
        DocsDatabase.OverrideDirForTests = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>A query orthogonal to every vector the fixtures hand out: its cosine is exactly 0.</summary>
    private static readonly float[] Orthogonal = [0f, 1f];

    /// <summary>Enough text that the chunker yields a chunk per page.</summary>
    private static string Body(string marker) => string.Join(" ",
        Enumerable.Range(0, 200).Select(i => i % 20 == 0 ? marker : $"word{i % 11}"));

    // ── @Docs ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A corpus in the state the product documents as permanent: the first chunks carry a vector,
    /// the rest were persisted without one.
    /// </summary>
    private async Task<DocsIndexService> DocsAsync(float threshold, int embedFirst)
    {
        var served = 0;
        var client = new FakeInferenceProvider
        {
            OnEmbedding = _ => served++ < embedFirst ? [1f, 0f] : null,
        };

        var docs = new DocsIndexService(
            client,
            new InferpalConfig { RagSimilarityThreshold = threshold })
        {
            CrawlForTests = (_, _) => Task.FromResult(new List<DocCrawler.Page>
            {
                new("https://docs.example.com/a", "Alpha", Body("alpha")),
                new("https://docs.example.com/b", "Beta",  Body("beta")),
            }),
        };
        await docs.AddOrReindexAsync(
            DocSite.Create("https://docs.example.com/", "Example"), progress: null, CancellationToken.None);
        return docs;
    }

    [Fact]
    public async Task Docs_AtThresholdZero_AChunkWithNoVector_IsNotReportedAsACosine()
    {
        var docs = await DocsAsync(threshold: 0f, embedFirst: 1);

        // WITNESS: the corpus really is mixed — something was embedded and something was not.
        // Without both, this test would pass on an empty index and measure nothing.
        Assert.True(docs.ChunkCount > 1, "the corpus has too few chunks to be mixed.");
        Assert.True(docs.UnembeddedCount > 0, "nothing was left without a vector: no defect to see.");

        var hits = await docs.SearchAsync(Orthogonal, keywordFallback: null, 10, CancellationToken.None);

        // WITNESS: the embedded chunks still come back, so the assertion below is not satisfied by
        // an empty list — which is how a vacuous DoesNotContain passes forever.
        Assert.NotEmpty(hits);
        Assert.DoesNotContain(hits, h => h.IsCosine && h.Chunk.Embedding is not { Length: > 0 });
    }

    [Fact]
    public async Task Docs_AtThresholdZero_ARealVectorOrthogonalToTheQuery_IsStillACosineHit()
    {
        // REFERENCE ARM: cosine 0 is a genuine similarity when there IS a vector. A fix that drops
        // everything scoring zero passes the test above and fails this one.
        var docs = await DocsAsync(threshold: 0f, embedFirst: 1_000);

        Assert.Equal(0, docs.UnembeddedCount);

        var hits = await docs.SearchAsync(Orthogonal, keywordFallback: null, 10, CancellationToken.None);

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.True(h.IsCosine));
    }

    [Fact]
    public async Task Docs_AtTheDefaultThreshold_NoVectorlessChunkGetsThrough()
    {
        // REFERENCE ARM: at the shipped default the sentinel never clears the gate, so this says
        // nothing about the fix — it says the defect needs the threshold the user can type.
        var docs = await DocsAsync(threshold: new InferpalConfig().RagSimilarityThreshold, embedFirst: 1);

        var hits = await docs.SearchAsync(Orthogonal, keywordFallback: null, 10, CancellationToken.None);

        Assert.DoesNotContain(hits, h => h.Chunk.Embedding is not { Length: > 0 });
    }

    // ── search_codebase — the same line, the same sentinel ────────────────────

    private async Task<ProjectIndexService> CodeAsync(float threshold, int embedFirst)
    {
        var root = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(root);
        for (var i = 0; i < 4; i++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, $"Widget{i}.cs"),
                $"namespace N;\npublic class Widget{i}\n{{\n    public void Run() {{ /* {Body("widget")} */ }}\n}}\n");
        }

        var served = 0;
        var client = new FakeInferenceProvider
        {
            OnEmbedding = _ => served++ < embedFirst ? [1f, 0f] : null,
        };

        var index = new ProjectIndexService(
            client,
            new InferpalConfig { RagEnabled = true, RagSimilarityThreshold = threshold },
            new LspSemanticProvider());
        _services.Add(index);
        index.StartIndexing(root);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((index.IsIndexing || index.ChunkCount == 0) && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.False(index.IsIndexing, $"the pass never finished: {index.Status}");
        return index;
    }

    [Fact]
    public async Task Code_AtThresholdZero_AChunkWithNoVector_IsNotReportedAsACosine()
    {
        var index = await CodeAsync(threshold: 0f, embedFirst: 1);

        Assert.True(index.ChunkCount > 1, "the corpus has too few chunks to be mixed.");

        var hits = await index.SearchAsync(Orthogonal, keywordFallback: null, 10, CancellationToken.None);

        // WITNESS: the search really returned something, so the assertion below is not satisfied by
        // an empty list — which is how a vacuous DoesNotContain passes forever.
        Assert.NotEmpty(hits);
        Assert.DoesNotContain(hits, h => h.IsCosine && h.Chunk.Embedding is not { Length: > 0 });
    }

    [Fact]
    public async Task Code_AtThresholdZero_ARealVectorOrthogonalToTheQuery_IsStillACosineHit()
    {
        // REFERENCE ARM, as above: the discriminator is the vector, not the number.
        var index = await CodeAsync(threshold: 0f, embedFirst: 1_000);

        var hits = await index.SearchAsync(Orthogonal, keywordFallback: null, 10, CancellationToken.None);

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.True(h.IsCosine));
    }
}
