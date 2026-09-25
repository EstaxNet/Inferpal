using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A generic OpenAI-compatible server that says its loaded window is heard, like LM Studio. llama-server loads
/// 4 096 tokens by default; with the configured window as the only one known, past the loaded one every question
/// was refused while compaction waited for a limit it never reached. The payloads are the shapes the servers'
/// own sources define: vLLM's <c>ModelCard.max_model_len</c> on <c>/v1/models</c>, llama-server's
/// <c>default_generation_settings.n_ctx</c> on <c>/props</c> (the slot's window: its tests assert
/// <c>n_ctx / n_slots</c>).
/// </summary>
public class GenericLoadedWindowTests
{
    // ⚠ The probe's production budget is 5 s for BOTH endpoints; on a loaded CI runner that is not 5 s, and a probe cut
    // short answers "unknown" — the llama-server case failed exactly so, in exactly 5 s. A test that waits gets 30.
    private static OpenAiCompatibleClient ClientFor(LoopbackHttpServer server) =>
        new(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl + "/v1" })
        {
            LoadedWindowProbeBudget = TimeSpan.FromSeconds(30),
        };

    [Fact]
    public async Task AVllmServer_ItsModelsMaxLength_IsTheLoadedWindow()
    {
        using var server = new LoopbackHttpServer(path => path == "/v1/models"
            ? """{"object":"list","data":[{"id":"other","object":"model","max_model_len":32768},{"id":"Qwen/Qwen3-8B","object":"model","max_model_len":4096}]}"""
            : null);

        Assert.Equal(4096, await ClientFor(server).GetLoadedContextWindowAsync("Qwen/Qwen3-8B", CancellationToken.None));
    }

    [Fact]
    public async Task ALlamaServer_ItsSlotWindow_IsTheLoadedWindow_NotTheTrainedOne()
    {
        using var server = new LoopbackHttpServer(path => path switch
        {
            "/v1/models" => """{"object":"list","data":[{"id":"model.gguf","meta":{"n_ctx_train":131072}}]}""",
            "/props"     => """{"default_generation_settings":{"params":{"seed":-1},"n_ctx":4096},"total_slots":1}""",
            _            => null,
        });

        Assert.Equal(4096, await ClientFor(server).GetLoadedContextWindowAsync("model.gguf", CancellationToken.None));
    }

    [Fact]
    public async Task TheRouterOfAMultiModelLlamaServer_SaysNothing()
    {
        using var server = new LoopbackHttpServer(path => path == "/props"
            ? """{"default_generation_settings":{"params":{},"n_ctx":0}}"""
            : null);

        Assert.Null(await ClientFor(server).GetLoadedContextWindowAsync("m", CancellationToken.None));
    }

    [Fact]
    public async Task AServerThatSaysNeither_IsUnknown()
    {
        // Reference arm: both places are asked, nothing is invented.
        using var server = new LoopbackHttpServer(_ => null);

        Assert.Null(await ClientFor(server).GetLoadedContextWindowAsync("m", CancellationToken.None));
        Assert.Contains("/v1/models", server.Paths);
        Assert.Contains("/props", server.Paths);
    }

    [Fact]
    public async Task AnOversizedRequest_IsRefusedWithItsBreakdown_BeforeItIsSent()
    {
        using var server = new LoopbackHttpServer(path => path == "/props"
            ? """{"default_generation_settings":{"n_ctx":4096}}"""
            : null);
        var messages = new List<ChatMessageDto>
        {
            new("system", "base"),
            new("user", "Review this.\n```\n" + new string('c', 40_000) + "\n```"),
        };

        var ex = await Assert.ThrowsAsync<AgentHttpException>(() => ClientFor(server).SendChatAsync(
            "m", messages, EmptyToolRegistry.Instance, onToken: null, CancellationToken.None));

        Assert.Contains("4096", ex.Message);
        Assert.DoesNotContain(server.Paths, p => p.StartsWith("/v1/chat/completions", StringComparison.Ordinal));
    }
}
