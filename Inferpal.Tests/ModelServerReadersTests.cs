using Inferpal.Config;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What the model servers' clients read the same way: a server's address ("/v1" is the compatible suffix, never part of
/// Ollama's own paths), an answer that holds no vector, the key a request carries.
/// </summary>
public class ModelServerReadersTests
{
    private static LoopbackHttpServer Embeddings(string answer) =>
        new((path, _) => path.StartsWith("/api/embeddings", StringComparison.Ordinal) ? answer : null);

    [Fact]
    public async Task AnOllamaAddressEndingInV1_IsItsRoot_AsTheTestButtonReadsIt()
    {
        // Ollama's documented compatible base is ".../v1": the Test button found Ollama there, and every request went to
        // ".../v1/api/…", which answers 404.
        using var server = Embeddings("""{"embedding":[1.0,0.0]}""");
        var client = new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl + "/v1/" });

        Assert.NotNull(await client.GetEmbeddingAsync("short", "embed-model", CancellationToken.None));
        Assert.Contains(server.Paths, p => p.StartsWith("/api/embeddings", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEmptyVector_IsNoVector_AsTheOtherClientAndTheIndexReadIt()
    {
        using var server = Embeddings("""{"embedding":[]}""");
        var client = new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl });

        Assert.Null(await client.GetEmbeddingAsync("short", "embed-model", CancellationToken.None));
    }

    [Fact]
    public async Task AKeyPastedWithSpaces_IsSentTrimmed_ByTheProbe_AsByTheChat()
    {
        using var server = new LoopbackHttpServer(path => path.EndsWith("/models", StringComparison.Ordinal) ? """{"data":[]}""" : null);

        await ProviderProbe.DetectAsync(server.BaseUrl + "/v1", " sk-abc ", CancellationToken.None);

        Assert.Contains("Bearer sk-abc", server.Authorizations);
    }
}
