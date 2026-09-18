using System.Collections.Generic;
using System.IO;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>search_codebase</c> inferred a GLOBAL fact — "this search is semantic" — from the score of the
/// FIRST result, when a result's provenance was written down nowhere.
/// </summary>
/// <remarks>
/// <para>
/// The search is <b>hybrid</b>: cosine ⊕ BM25 fused with RRF. A result found by the lexical side
/// alone has no cosine and came back as <c>0f</c>; one from the vector side has one. The report read
/// <c>results[0].Score is &gt; 0f and &lt; 1.001f</c> — that is, it asked the <b>number</b> a
/// question only <b>provenance</b> can answer.
/// </para>
/// <para>
/// ⚠ <b>Two opposite failures, and the first one hits the very case the lexical half exists for.</b>
/// (1) An exact-identifier query — <c>RagDatabase</c> — is precisely what BM25 catches when weak
/// local embeddings dilute it (the site's own comment says so). That result then lands first with
/// <c>0f</c>: the whole report announces itself "keyword" and <b>none</b> of the other results shows
/// its score, although they have one. The model reads that the semantic index did not serve.
/// (2) The other way round, on the purely lexical fallback, a BM25 score that falls under 1.001 — a
/// frequent term, a short document — passed for a cosine: a <b>wrong number</b>, announced
/// "semantic".
/// </para>
/// <para>
/// ⚠ <c>HybridSearch.cs</c> is described in the maintainer's notes as "pure/testable" and was named
/// by NO test — found by sweeping the files under <c>Services/</c> that no test mentions.
/// </para>
/// </remarks>
public class RagSearchProvenanceTests
{
    private static RagChunk Chunk(string path) =>
        new() { RelPath = path, Content = "x", StartLine = 1, EndLine = 2 };

    private static RagHit Cosine(float score) => new(Chunk("a.cs"), score, IsCosine: true);
    private static RagHit Lexical(float score) => new(Chunk("b.cs"), score, IsCosine: false);

    // ── The label reads EVERY result, never just the first ──────────────────

    [Fact]
    public void AnExactIdentifierHitInFirstPlace_DoesNotTurnTheWholeReportIntoKeyword()
    {
        // The case the lexical half was added for: BM25 catches `RagDatabase` first, the ones after
        // it come from the vector side. This is not a keyword search.
        List<RagHit> hits = [Lexical(0f), Cosine(0.83f), Cosine(0.79f)];

        Assert.Equal("hybrid", RagResultPresentation.ModeLabel(embeddingsRan: true, hits));
    }

    [Fact]
    public void AllFromTheVectorSide_IsSemantic()
    {
        List<RagHit> hits = [Cosine(0.9f), Cosine(0.7f)];
        Assert.Equal("semantic", RagResultPresentation.ModeLabel(embeddingsRan: true, hits));
    }

    [Fact]
    public void NoneFromTheVectorSide_IsKeyword()
    {
        List<RagHit> hits = [Lexical(4.2f), Lexical(1.1f)];
        Assert.Equal("keyword", RagResultPresentation.ModeLabel(embeddingsRan: true, hits));
    }

    [Fact]
    public void WithoutEmbeddings_ItIsKeyword_WhateverTheNumbers()
    {
        // ⚠ Reference arm for failure (2): a BM25 score under 1.001 looks exactly like a cosine.
        // It is not one, and nothing in the number says so.
        List<RagHit> hits = [Lexical(0.84f)];
        Assert.Equal("keyword", RagResultPresentation.ModeLabel(embeddingsRan: false, hits));
    }

    // ── A score is shown only when it IS a similarity ────────────────────────

    [Fact]
    public void OnlyACosine_IsShownAsAScore()
    {
        // Failure (1): these two had a cosine and did not show it, because a THIRD result — lexical
        // — held first place.
        List<RagHit> hits = [Lexical(0f), Cosine(0.83f)];
        var label = RagResultPresentation.ModeLabel(embeddingsRan: true, hits);

        Assert.True(RagResultPresentation.ShowsScore(label, hits[1]));
        // And the lexical one shows nothing: a number not comparable with the cosines above it is
        // worse than no number.
        Assert.False(RagResultPresentation.ShowsScore(label, hits[0]));
    }

    [Fact]
    public void ABm25ScoreUnderOne_IsNeverShownAsASimilarity()
    {
        // Failure (2), from the other end: the purely lexical fallback shows no score at all.
        List<RagHit> hits = [Lexical(0.84f)];
        var label = RagResultPresentation.ModeLabel(embeddingsRan: true, hits);

        Assert.False(RagResultPresentation.ShowsScore(label, hits[0]));
    }

    // ── The provenance is WRITTEN DOWN, not guessed ─────────────────────────

    [Fact]
    public void AFusedHit_IsACosineOnlyIfTheVectorSideRankedIt()
    {
        // ⚠ Nothing held this half: the first round of sabotages showed that writing `IsCosine: true`
        // everywhere reddened not one test. The whole fix rests on that boolean.
        var chunks = new List<RagChunk> { Chunk("0.cs"), Chunk("1.cs"), Chunk("2.cs") };
        // The vector side ranked 2 and 0; 1 comes from the lexical side only.
        var cosines = new Dictionary<int, float> { [2] = 0.91f, [0] = 0.62f };

        var hits = RagHit.FromFusion([1, 2, 0], chunks, cosines, topK: 10);

        Assert.Equal(3, hits.Count);
        Assert.False(hits[0].IsCosine);          // lexical seul
        Assert.Equal(0f, hits[0].Score);
        Assert.True(hits[1].IsCosine);
        Assert.Equal(0.91f, hits[1].Score);
        Assert.True(hits[2].IsCosine);
        Assert.Equal(0.62f, hits[2].Score);
    }

    [Fact]
    public void TheFusionHonoursTopK_AndItsOrder()
    {
        // Reference arm: the builder reorders nothing and honours the cap.
        var chunks  = new List<RagChunk> { Chunk("0.cs"), Chunk("1.cs"), Chunk("2.cs") };
        var cosines = new Dictionary<int, float> { [0] = 0.5f };

        var hits = RagHit.FromFusion([2, 0, 1], chunks, cosines, topK: 2);

        Assert.Equal(2, hits.Count);
        Assert.Equal("2.cs", hits[0].Chunk.RelPath);
        Assert.Equal("0.cs", hits[1].Chunk.RelPath);
    }

    // ── The rendering reads the provenance, not the number ───────────────────

    [Fact]
    public void TheToolNoLongerAsksTheNumberWhatOnlyProvenanceKnows()
    {
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(
            RepoRoot(), "Inferpal.Core", "Services", "Tools", "SemanticSearchTool.cs"));

        // WITNESS: this really is the file that renders the report.
        Assert.Contains("Codebase search", code, StringComparison.Ordinal);

        Assert.Contains("RagResultPresentation", code, StringComparison.Ordinal);
        // The exact shape of the defect: a global fact read off the first result.
        Assert.DoesNotContain("results[0].Score", code, StringComparison.Ordinal);
        Assert.DoesNotContain("1.001", code, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
