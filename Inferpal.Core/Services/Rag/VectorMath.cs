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
