using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ Ollama refuses a text longer than the embedding model's window — it does not truncate it. An indexed chunk past the
/// window got no vector at all, and a run of them opened the embedding breaker: semantic search fell back to keywords for
/// the whole index. The text is embedded in halves, and the halves averaged.
/// </summary>
public class EmbeddingTooLongTests
{
    private const int Window = 100;

    /// <summary>Ollama as it answers: a vector for a text that fits, the refusal for one that does not.</summary>
    private static LoopbackHttpServer Server(string refusal = "the input length exceeds the context length") => new(
        (path, body) => path.StartsWith("/api/embeddings", StringComparison.Ordinal)
            ? JsonDocument.Parse(body).RootElement.GetProperty("prompt").GetString()!.Length <= Window
                ? """{"embedding":[1.0,0.0]}"""
                : $$"""{"error":"{{refusal}}"}"""
            : null,
        (path, body) => path.StartsWith("/api/embeddings", StringComparison.Ordinal)
                        && JsonDocument.Parse(body).RootElement.GetProperty("prompt").GetString()!.Length > Window ? 500 : 200);

    private static string Lines(int count) => string.Join("\n", Enumerable.Range(0, count).Select(i => $"line {i:D3} of code"));

    [Fact]
    public async Task AChunkPastTheWindow_IsEmbeddedInParts_AndGetsAVector()
    {
        using var server = Server();
        var client = new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl });
        var chunk = Lines(30);   // ~540 characters: five times the window

        var vector = await client.GetEmbeddingAsync(chunk, "embed-model", CancellationToken.None);

        Assert.NotNull(vector);
        Assert.Equal(1f, vector![0], 3);
        Assert.True(server.Paths.Count(p => p.StartsWith("/api/embeddings", StringComparison.Ordinal)) > 2);
        // And the breaker took none of the refusals for a failure: the next text is embedded.
        Assert.NotNull(await client.GetEmbeddingAsync("short", "embed-model", CancellationToken.None));
    }

    /// <summary>Reference arm: another failure of the server is a failure — nothing is split.</summary>
    [Fact]
    public async Task AnotherRefusal_IsNotSplit()
    {
        using var server = Server(refusal: "model runner has unexpectedly stopped");
        var client = new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl });

        Assert.Null(await client.GetEmbeddingAsync(Lines(30), "embed-model", CancellationToken.None));
        Assert.Equal(1, server.Paths.Count(p => p.StartsWith("/api/embeddings", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("aaaa\nbbbb\ncccc\ndddd", "aaaa\nbbbb\n", "cccc\ndddd")]   // at the line break nearest the middle
    [InlineData("abcdefgh", "abcd", "efgh")]                                // no line break: the middle
    [InlineData("a\nbbbbbbbbbbbbbbbbbbbb", "a\nbbbbbbbbb", "bbbbbbbbbbb")]     // a line break far from it: the middle
    [InlineData("ab\n", "a", "b\n")]                                       // never a cut at the very end
    public void Halves_CutAtALineBreak_NearTheMiddle(string text, string first, string second) =>
        Assert.Equal((first, second), OllamaClient.Halves(text));

    [Fact]
    public void Pooled_WeighsEachHalfByItsText_AndIsUnitLength()
    {
        var pooled = OllamaClient.Pooled([1f, 0f], 3, [0f, 1f], 1);

        Assert.True(pooled[0] > pooled[1]);
        Assert.Equal(1.0, Math.Sqrt(pooled.Sum(x => (double)x * x)), 3);
    }
}
