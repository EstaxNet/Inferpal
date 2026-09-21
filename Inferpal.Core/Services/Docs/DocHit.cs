namespace Inferpal.Services.Docs;

/// <summary>
/// One result of the documentation search, carrying <b>which half of the search produced it</b>.
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
/// <para>
/// ⚠ The twin of <see cref="Rag.RagHit"/>, and for the same reason: returned as
/// <c>(chunk, float)</c>, a lexical-only hit comes back with <c>0f</c> and the only way left to ask
/// "is this a similarity?" is to look at the number — wrong in both directions.
/// </para>
/// <para>
/// ⚠ It bites harder on this corpus than on the code index: the lexical side is the <b>only</b> half
/// that reaches the chunks held without a vector, so on a corpus with an embedding hole the results
/// that matter most are exactly the ones whose score is not a similarity.
/// </para>
/// </remarks>
internal readonly record struct DocHit(DocChunk Chunk, float Score, bool IsCosine);
