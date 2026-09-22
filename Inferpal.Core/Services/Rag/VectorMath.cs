namespace Inferpal.Services.Rag;

/// <summary>Vector arithmetic shared by the code index and the documentation index.</summary>
internal static class VectorMath
{
    /// <summary>
    /// Cosine similarity of two vectors; 0 when their dimensions differ (vectors from different
    /// embedding models — truncating would produce a meaningless score) or when either is null.
    /// </summary>
    public static float Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
        float dot = 0f, normA = 0f, normB = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            dot   += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return normA > 0f && normB > 0f
            ? dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB))
            : 0f;
    }

    /// <summary>
    /// The vector side of a hybrid search: the chunks that <b>have</b> a vector, scored by cosine
    /// against <paramref name="query"/>, keeping those at or above <paramref name="threshold"/>,
    /// best first, capped to <paramref name="pool"/>. Indices are into <paramref name="chunks"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>Having a vector is a precondition, never a score.</b> Written inline, this side reads
    /// <c>c.Embedding is { Length: &gt; 0 } ? Cosine(…) : 0f</c> and then filters on the threshold —
    /// so the <c>0f</c> that means "never embedded" clears a threshold of <b>0</b>, which the
    /// settings panel offers over 0–1 and nothing clamps. A chunk that was never embedded is then
    /// ranked by the vector side and reported as a <b>cosine of 0.00</b>.
    /// </para>
    /// <para>
    /// ⚠ That is the mirror of what <see cref="RagHit.IsCosine"/> exists to prevent: provenance
    /// belongs in the data, and a chunk with no vector has none on this side. Both indexes hold such
    /// chunks by design — the pass continues without embeddings when the model is unavailable — and
    /// on <c>@Docs</c> that state is permanent until a re-index.
    /// </para>
    /// <para>
    /// ⚠ The discriminator is the <b>vector</b>, not the number: a real embedding orthogonal to the
    /// query has a cosine of exactly 0 and is a legitimate semantic hit, so dropping everything that
    /// scores 0 is a different — and wrong — rule.
    /// </para>
    /// </remarks>
    public static List<(int Idx, float Cos)> RankBySimilarity<T>(
        IReadOnlyList<T> chunks, Func<T, float[]?> embedding, float[] query, float threshold, int pool)
    {
        var ranked = new List<(int Idx, float Cos)>();
        for (int i = 0; i < chunks.Count; i++)
        {
            if (embedding(chunks[i]) is not { Length: > 0 } vector) continue;
            var cos = Cosine(query, vector);
            if (cos >= threshold) ranked.Add((i, cos));
        }

        ranked.Sort((a, b) => b.Cos.CompareTo(a.Cos));
        return ranked.Count > pool ? ranked.GetRange(0, pool) : ranked;
    }

    /// <summary>A vector as its stored BLOB: raw float32 in platform (little-endian) order, no header.</summary>
    public static byte[] ToBlob(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>The inverse of <see cref="ToBlob"/>; empty when the BLOB is not a whole number of float32.</summary>
    public static float[] FromBlob(byte[] blob)
    {
        if (blob.Length % sizeof(float) != 0) return [];
        var floats = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, floats, 0, blob.Length);
        return floats;
    }
}
