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
}
