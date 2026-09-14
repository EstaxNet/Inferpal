using Inferpal.Config;
using Inferpal.Services.Inference;
using Inferpal.Services.Signals;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A ghost-text request whose stream carries a server error counts as a failure.
/// </summary>
/// <remarks>
/// Ollama and LM Studio report some failures inside a 200 response — a model runner that stopped, a
/// model that does not do completions. The chat loops turn that line into an error; the ghost-text
/// loops ignored it and counted the request as a success, recorded as soon as the headers arrived.
/// Nothing was shown, nothing traced, and the circuit breaker never opened: every pause in typing sent
/// the same failing request again.
/// </remarks>
[Collection(SignalCollection.Name)]
public sealed class FimStreamErrorTests : IDisposable
{
    // Ghost text yields to a chat turn marked busy in the signal folder: never the real one.
    private readonly SignalScratchDir _signals = new();

    public void Dispose() => _signals.Dispose();

    /// <summary>Consecutive failures after which the client stops sending (InferenceProviderBase).</summary>
    private const int BreakerThreshold = 5;

    /// <summary>
    /// Calls until the stand-in has served <see cref="BreakerThreshold"/> requests, then three more, and
    /// returns how many reached it. A call that yields to a chat turn elsewhere reaches nothing, so the
    /// requests served — not the calls made — are what is compared.
    /// </summary>
    private static async Task<int> RequestsServedAsync(LoopbackHttpServer server, string path, Func<Task> call)
    {
        int Served() => server.Paths.Count(p => p.StartsWith(path, StringComparison.Ordinal));

        for (var i = 0; i < 200 && Served() < BreakerThreshold; i++) await call();
        Assert.Equal(BreakerThreshold, Served());   // witness: the stand-in really answered

        for (var i = 0; i < 3; i++) await call();
        return Served();
    }

    [Fact]
    public async Task Ollama_AnErrorInsideTheStream_OpensTheBreaker()
    {
        using var server = new LoopbackHttpServer(p => p.StartsWith("/api/generate", StringComparison.Ordinal)
            ? "{\"error\":\"model runner has unexpectedly stopped\"}\n"
            : null);
        var client = new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl });

        var served = await RequestsServedAsync(server, "/api/generate",
            () => client.StreamFimAsync("a", "b", 8, 0.2, _ => { }, CancellationToken.None, "m"));

        Assert.Equal(BreakerThreshold, served);
    }

    [Fact]
    public async Task LmStudio_AnErrorInsideTheStream_OpensTheBreaker()
    {
        using var server = new LoopbackHttpServer(p => p.StartsWith("/v1/completions", StringComparison.Ordinal)
            ? "data: {\"error\":{\"message\":\"model does not support completions\"}}\n\ndata: [DONE]\n\n"
            : null);
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl });

        var served = await RequestsServedAsync(server, "/v1/completions",
            () => client.StreamFimAsync("a", "b", 8, 0.2, _ => { }, CancellationToken.None, "m"));

        Assert.Equal(BreakerThreshold, served);
    }

    /// <summary>Witness: a healthy stream keeps reaching the server and delivers its text.</summary>
    [Fact]
    public async Task Ollama_AHealthyStream_KeepsReachingTheServer()
    {
        using var server = new LoopbackHttpServer(p => p.StartsWith("/api/generate", StringComparison.Ordinal)
            ? "{\"response\":\"x\",\"done\":false}\n{\"response\":\"\",\"done\":true}\n"
            : null);
        var client = new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl });
        var tokens = new List<string>();

        var served = await RequestsServedAsync(server, "/api/generate",
            () => client.StreamFimAsync("a", "b", 8, 0.2, t => { lock (tokens) tokens.Add(t); }, CancellationToken.None, "m"));

        Assert.Equal(BreakerThreshold + 3, served);
        Assert.Contains("x", tokens);
    }
}
