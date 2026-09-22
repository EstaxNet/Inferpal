using System.IO;
using System.Linq;
using Inferpal.Config;
using Inferpal.Services.Docs;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A chunk held <b>without a vector</b> never comes back as a semantic hit.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Both hybrid searches scored the vector side as
/// <c>c.Embedding is { Length: &gt; 0 } ? Cosine(…) : 0f</c> and then kept what cleared
/// <c>RagSimilarityThreshold</c>. That <c>0f</c> is a <b>sentinel for "there is no vector"</b>, not a
/// similarity — and the threshold is a plain setting the user types, offered over 0–1 with no clamp.
/// At <b>0</b> the sentinel clears the gate: a chunk that was never embedded is ranked by the vector
/// side and reported as a <b>cosine of 0.00</b>.
/// </para>
/// <para>
/// ⭐ It is the mirror of the defect the provenance work closed — there, a purely lexical hit scored
/// <c>0f</c> and passed for a similarity of zero; here a chunk with no vector at all does. That fix
/// wrote <c>IsCosine</c> so provenance would live in the data rather than be inferred from a number,
/// and then the vector side went on <b>manufacturing</b> the number it inferred from.
/// </para>
/// <para>
/// ⚠ Holding chunks without vectors is designed behaviour on both indexes
/// (<c>chunk.Embedding = emb; // null if model unavailable</c>, "index continues without embeddings")
/// and on <c>@Docs</c> it is permanent until a re-index. And 0 is exactly the threshold someone
/// getting no results would reach for.
/// </para>
/// <para>
/// ⚠ The discriminator is <b>having a vector</b>, never <b>scoring above zero</b>: a real embedding
/// orthogonal to the query has a cosine of exactly 0 and is a legitimate semantic hit. Every pair
/// below carries both halves so the cheap fix — drop anything scoring 0 — fails a reference arm.
/// </para>
/// <para>
/// The rule lives in ONE writer, <see cref="VectorMath.RankBySimilarity"/>, which is pure — so it is
/// pinned directly, and the documentation index exercises the same writer end to end. Standing up a
/// second index service here would add no coverage of the rule.
/// </para>
/// </remarks>
[Collection(DocsDatabaseCollection.Name)]
public sealed class NoVectorIsNotASimilarityTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"novector-{Guid.NewGuid():N}");

    public NoVectorIsNotASimilarityTests() => DocsDatabase.OverrideDirForTests = _dir;

    public void Dispose()
    {
        DocsDatabase.OverrideDirForTests = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>A query orthogonal to every vector below: its cosine against them is exactly 0.</summary>
    private static readonly float[] Orthogonal = [0f, 1f];

    // ── The writer, pinned directly ──────────────────────────────────────────

    /// <summary>Stands in for any chunk type: the ranker reads a vector through a selector.</summary>
    private sealed record Chunk(float[]? Embedding);

    private static List<(int Idx, float Cos)> Rank(float threshold, params float[]?[] vectors) =>
        VectorMath.RankBySimilarity(
            vectors.Select(v => new Chunk(v)).ToList(), c => c.Embedding, Orthogonal, threshold, pool: 50);

    [Fact]
    public void AtThresholdZero_AChunkWithNoVector_IsNotRanked()
    {
        // Index 0 has no vector at all; index 1 has one, orthogonal, so its cosine is exactly 0.
        var ranked = Rank(0f, null, [1f, 0f]);

        // WITNESS: the real vector IS ranked, so the assertion below is not satisfied by an empty
        // result — which is how "drop everything" would pass.
        Assert.Contains(ranked, r => r.Idx == 1);
        Assert.DoesNotContain(ranked, r => r.Idx == 0);
    }

    [Fact]
    public void AtThresholdZero_ARealVectorOrthogonalToTheQuery_IsStillRanked()
    {
        // REFERENCE ARM: cosine 0 is a genuine similarity when there IS a vector. A fix that drops
        // everything scoring zero passes the test above and fails this one.
        var ranked = Rank(0f, [1f, 0f]);

        Assert.Single(ranked);
        Assert.Equal(0f, ranked[0].Cos);
    }

    [Fact]
    public void AnEmptyVector_CountsAsNoVector()
    {
        // The other shape the sentinel takes: a stored BLOB that decoded to nothing.
        var ranked = Rank(0f, [], [1f, 0f]);

        Assert.DoesNotContain(ranked, r => r.Idx == 0);
    }

    [Fact]
    public void AboveTheThreshold_TheBestComesFirst()
    {
        // WITNESS that the ranker ranks: without it these tests would pass on a function that
        // returned everything it was given, in any order.
        var ranked = VectorMath.RankBySimilarity(
            new List<Chunk> { new([1f, 0f]), new([0f, 1f]), new([1f, 1f]) },
            c => c.Embedding, Orthogonal, threshold: 0.1f, pool: 50);

        Assert.Equal(2, ranked.Count);           // the orthogonal one is below the threshold
        Assert.Equal(1, ranked[0].Idx);          // cosine 1 before cosine ~0.707
    }

    // ── And end to end, through the index that hydrates vectors from disk ─────

    private static string Body(string marker) => string.Join(" ",
        Enumerable.Range(0, 200).Select(i => i % 20 == 0 ? marker : $"word{i % 11}"));

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
        Assert.True(docs.ChunkCount > 1, "the corpus has too few chunks to be mixed.");
        Assert.True(docs.UnembeddedCount > 0, "nothing was left without a vector: no defect to see.");

        var hits = await docs.SearchAsync(Orthogonal, keywordFallback: null, 10, CancellationToken.None);

        Assert.NotEmpty(hits);
        Assert.DoesNotContain(hits, h => h.IsCosine && h.Chunk.Embedding is not { Length: > 0 });
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
}
