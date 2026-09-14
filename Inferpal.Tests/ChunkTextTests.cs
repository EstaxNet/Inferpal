using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The chunk fingerprint and the vector encoding every index shares. Both are persisted: a hash in
/// another format misses every stored embedding and re-embeds each indexed project on the next start,
/// and a blob read another way turns stored vectors into noise.
/// </summary>
public class ChunkTextTests
{
    [Fact]
    public void TheHash_IsLowercaseHexMd5OfTheUtf8Text()
    {
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", ChunkText.Hash("abc"));
        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", ChunkText.Hash(""));
    }

    [Fact]
    public void TheTokenEstimate_CountsAtLeastOnePerLine_AndStopsAtTheLastLine()
    {
        string[] lines = ["", "abcd", "abcdefgh"];
        Assert.Equal(1 + 1 + 2, ChunkText.EstimateTokens(lines, 0, 10));
        Assert.Equal(1, ChunkText.EstimateTokens(lines, 1, 1));
    }

    [Fact]
    public void AVector_RoundTripsThroughItsBlob()
    {
        float[] vector = [1.5f, -2f, 0.25f];
        var blob = VectorMath.ToBlob(vector);

        Assert.Equal(3 * sizeof(float), blob.Length);
        Assert.Equal(vector, VectorMath.FromBlob(blob));
        Assert.Empty(VectorMath.FromBlob([1, 2, 3]));   // not a whole number of float32
    }
}
