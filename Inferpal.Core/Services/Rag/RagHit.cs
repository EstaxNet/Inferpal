namespace Inferpal.Services.Rag;

/// <summary>
/// One result of a hybrid search, carrying <b>which half of the search produced it</b>.
/// </summary>
/// <param name="Score">
/// Cosine similarity when <paramref name="IsCosine"/> is true; otherwise the lexical (BM25) score,
/// which is unbounded and not comparable with a cosine.
/// </param>
/// <param name="IsCosine">
/// True when the vector side ranked this chunk, so <paramref name="Score"/> is a similarity the
/// reader can compare with the others.
/// </param>
/// <remarks>
/// ⚠ The provenance is IN the data, never deduced from the score: returned as <c>(chunk, float)</c>,
/// a lexical-only hit comes back with <c>0f</c>, and the only way left to ask "is this a
/// similarity?" is to look at the number — which is wrong in both directions.
/// </remarks>
internal readonly record struct RagHit(RagChunk Chunk, float Score, bool IsCosine)
{
    /// <summary>
    /// Builds the hits of a fused (hybrid) ranking: a chunk is a cosine hit <b>iff the vector side
    /// ranked it</b>, which is the one fact the score cannot carry — a lexical-only hit scores
    /// <c>0f</c>, indistinguishable from a similarity of zero.
    /// </summary>
    internal static List<RagHit> FromFusion(
        IEnumerable<int> fusedOrder, IReadOnlyList<RagChunk> chunks,
        IReadOnlyDictionary<int, float> cosineByIndex, int topK) =>
        fusedOrder.Take(topK)
                  .Select(i =>
                  {
                      var (score, isCosine) = HybridProvenance.OfFused(i, cosineByIndex);
                      return new RagHit(chunks[i], score, isCosine);
                  })
                  .ToList();
}

/// <summary>
/// Where one hit of a fused ranking came from — <b>written once, for every index</b>.
/// </summary>
/// <remarks>
/// ⚠ The repository has TWO hybrid indexes over the same machinery (code and documentation) and the
/// rule they share is <i>a chunk is a cosine hit iff the vector side ranked it</i>. Spelled out at
/// each fusion site it is three characters away from <c>GetValueOrDefault(i, 0f)</c> alone, which
/// silently turns every lexical-only hit into a similarity of zero.
/// </remarks>
internal static class HybridProvenance
{
    /// <param name="cosineByIndex">The cosine of every chunk the VECTOR side ranked — membership is
    /// the fact, the value is only what to display.</param>
    internal static (float Score, bool IsCosine) OfFused(
        int index, IReadOnlyDictionary<int, float> cosineByIndex) =>
        (cosineByIndex.GetValueOrDefault(index, 0f), cosineByIndex.ContainsKey(index));
}

/// <summary>
/// How a set of search hits is announced to the model. Pure/testable — the whole point is that these
/// two questions are answered from the <b>hits</b>, never inferred from one number.
/// </summary>
internal static class RagResultPresentation
{
    /// <summary>
    /// The one word that tells the model which half of the search produced this ranking, read from
    /// <b>every</b> hit.
    /// </summary>
    /// <remarks>
    /// ⚠ It was read from <c>hits[0]</c>: an exact-identifier query — the very case the lexical half
    /// exists for — puts a BM25-only hit first, and the whole report then announced itself
    /// <c>keyword</c> while hiding the similarity of every other result. The model reads that the
    /// semantic index did not serve.
    /// </remarks>
    internal static string ModeLabel(bool embeddingsRan, IReadOnlyList<RagHit> hits)
    {
        var cosines = 0;
        foreach (var h in hits) if (h.IsCosine) cosines++;
        return ModeLabel(embeddingsRan, hits.Count, cosines);
    }

    /// <summary>
    /// The core, in the two facts it actually needs — so the documentation index answers this
    /// question through the SAME reader as the code index rather than a copy of it.
    /// </summary>
    internal static string ModeLabel(bool embeddingsRan, int hitCount, int cosineCount)
    {
        if (!embeddingsRan || hitCount == 0) return "keyword";
        return cosineCount == 0 ? "keyword" : cosineCount == hitCount ? "semantic" : "hybrid";
    }

    /// <summary>Whether this hit's score may be shown as a similarity.</summary>
    /// <remarks>
    /// ⚠ A BM25 score is unbounded and not comparable with a cosine: one under 1.001 prints as
    /// <c>score 0.840</c> next to real similarities. A number the reader cannot compare is worse
    /// than no number — a lexical hit shows none, and the mode label says why.
    /// ⚠ Takes the two facts, not a hit: the documentation index carries a different chunk type and
    /// must not get a second copy of this decision. The mode label is NOT a parameter — it is
    /// computed from the same provenance, so passing it would invite answering per report what is
    /// true per hit.
    /// </remarks>
    internal static bool ShowsScore(bool isCosine, float score) => isCosine && score > 0f;
}
