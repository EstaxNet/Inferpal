using Inferpal.Config;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  An embedding refused while the server loads the model is retried; a failure is said, once.
//
//  Measured on LM Studio with the embedding model not yet loaded: the first request waits ~3 s for the
//  on-demand load, and a SECOND concurrent one gets HTTP 500 in 0.02 s. Indexing sends several, and
//  three quick 500s opened the embedding breaker: the published Linux host indexed a project with
//  "2 of 2 chunks without embedding" — twice, until the model happened to be loaded — while the
//  bare catch around the call said nothing about why.
//
//  The stand-in server answers a real socket; the production client talks to it.
// ──────────────────────────────────────────────────────────────────────────────────────────────
[Collection("Diagnostics")]
public class EmbeddingColdLoadTests
{
    private const string Vector = """{"object":"list","data":[{"object":"embedding","index":0,"embedding":[0.1,0.2,0.3]}]}""";

    private static OpenAiCompatibleClient Client(LoopbackHttpServer server) =>
        new(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl })
        {
            EmbeddingRetryDelays = [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50)],
        };

    private static int Calls(LoopbackHttpServer server) =>
        server.Paths.Count(p => p.StartsWith("/v1/embeddings", StringComparison.Ordinal));

    [Fact]
    public async Task A500WhileTheModelLoads_IsRetried_AndTheVectorArrives()
    {
        var n = 0;
        using var server = new LoopbackHttpServer(
            path => path.StartsWith("/v1/embeddings", StringComparison.Ordinal) ? Vector : null,
            status: _ => Interlocked.Increment(ref n) == 1 ? 500 : 200);

        var vector = await Client(server).GetEmbeddingAsync("code", "embed-model", CancellationToken.None);

        Assert.NotNull(vector);
        Assert.Equal([0.1f, 0.2f, 0.3f], vector);
        Assert.Equal(2, Calls(server));
    }

    [Fact]
    public async Task A4xx_IsNotRetried()
    {
        // Reference arm: a model the server does not have does not appear by waiting.
        using var server = new LoopbackHttpServer(
            path => path.StartsWith("/v1/embeddings", StringComparison.Ordinal) ? "{}" : null, status: _ => 404);

        Assert.Null(await Client(server).GetEmbeddingAsync("code", "embed-model", CancellationToken.None));
        Assert.Equal(1, Calls(server));
    }

    [Fact]
    public async Task AFailureIsSaid_OncePerModel_AndAgainAfterARecovery()
    {
        Inferpal.Services.Diagnostics.Clear();
        var failing = true;
        using var server = new LoopbackHttpServer(
            path => path.StartsWith("/v1/embeddings", StringComparison.Ordinal) ? Vector : null,
            status: _ => failing ? 500 : 200);
        var client = Client(server);

        Assert.Null(await client.GetEmbeddingAsync("a", "embed-model", CancellationToken.None));
        Assert.Equal(4, Calls(server));                                    // the first try and three retries
        await client.GetEmbeddingAsync("b", "embed-model", CancellationToken.None);
        var said = Inferpal.Services.Diagnostics.Snapshot().Where(e => e.Context == "Embedding").ToList();
        var line = Assert.Single(said);                                    // per chunk, said once
        Assert.Contains("embed-model", line.Detail);
        Assert.Contains("HTTP 500", line.Detail);

        failing = false;
        Assert.NotNull(await client.GetEmbeddingAsync("c", "embed-model", CancellationToken.None));
        failing = true;
        await client.GetEmbeddingAsync("d", "embed-model", CancellationToken.None);
        Assert.Equal(2, Inferpal.Services.Diagnostics.Snapshot().Count(e => e.Context == "Embedding"));
    }
}
