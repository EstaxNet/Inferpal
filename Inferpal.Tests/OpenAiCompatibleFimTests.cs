using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Inference;
using Inferpal.Services.Signals;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Inline completion on a server declared OpenAI-compatible: the server applies the model's FIM template itself.
/// llama-server (recognized by its <c>/props</c>) is asked through <c>/infill</c>, any other server through
/// <c>/v1/completions</c> with <c>suffix</c>; a server that rejects the request is said once and not asked again for
/// that model.
/// </summary>
[Collection(SignalCollection.Name)]
public sealed class OpenAiCompatibleFimTests : IDisposable
{
    // Ghost text yields to a chat turn marked busy in the signal folder: never the real one.
    private readonly SignalScratchDir _signals = new();

    public void Dispose() => _signals.Dispose();

    private static OpenAiCompatibleClient Client(LoopbackHttpServer server) =>
        new(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl + "/v1" });

    private static async Task<string> CompleteAsync(OpenAiCompatibleClient client, string model)
    {
        var text = new System.Text.StringBuilder();
        await client.StreamFimAsync("return ", ";\n}", 16, 0.2, t => text.Append(t), CancellationToken.None, model);
        return text.ToString();
    }

    private static int Served(LoopbackHttpServer server, string path) =>
        server.Paths.Count(p => p.StartsWith(path, StringComparison.Ordinal));

    [Fact]
    public async Task ALlamaServer_IsAskedThroughInfill_WithBothHalves()
    {
        using var server = new LoopbackHttpServer(p =>
            p.StartsWith("/props", StringComparison.Ordinal)  ? "{\"default_generation_settings\":{\"n_ctx\":4096}}"
            : p.StartsWith("/infill", StringComparison.Ordinal) ? "data: {\"content\":\"a + b\",\"stop\":false}\n\ndata: {\"content\":\"\",\"stop\":true}\n\n"
            : null);

        var client = Client(server);
        var text = await CompleteAsync(client, "coder");
        await CompleteAsync(client, "coder");

        Assert.Equal("a + b", text);
        Assert.Equal(1, Served(server, "/props"));            // recognized once, not on every pause in typing
        Assert.Equal(2, Served(server, "/infill"));
        Assert.Equal(0, Served(server, "/v1/completions"));
        var body = server.Bodies.First(b => b.Contains("input_prefix", StringComparison.Ordinal));
        Assert.Contains("\"input_suffix\"", body, StringComparison.Ordinal);
        Assert.Contains("\"model\":\"coder\"", body, StringComparison.Ordinal);
    }

    /// <summary>llama-server answers 501 for a model without FIM tokens: said, and that model is left alone.</summary>
    [Fact]
    public async Task ALlamaServerThatCannotInfill_IsHeld()
    {
        const string model = "no-fim-tokens-model";
        using var server = new LoopbackHttpServer(
            p => p.StartsWith("/props", StringComparison.Ordinal)  ? "{\"default_generation_settings\":{}}"
               : p.StartsWith("/infill", StringComparison.Ordinal) ? "{\"error\":{\"code\":501,\"message\":\"Infill is not supported by this model\"}}"
               : null,
            p => p.StartsWith("/infill", StringComparison.Ordinal) ? 501 : 200);
        var client = Client(server);

        await CompleteAsync(client, model);
        await CompleteAsync(client, model);

        Assert.Equal(1, Served(server, "/infill"));
        Assert.True(client.InlineCompletionRefused(model));
    }

    [Fact]
    public async Task AnyOtherServer_IsAskedThroughCompletions_WithTheSuffix()
    {
        using var server = new LoopbackHttpServer(p =>
            p.StartsWith("/v1/completions", StringComparison.Ordinal)
                ? "data: {\"choices\":[{\"text\":\"a + b\"}]}\n\ndata: [DONE]\n\n"
                : null);

        var text = await CompleteAsync(Client(server), "coder");

        Assert.Equal("a + b", text);
        Assert.Equal(1, Served(server, "/props"));            // asked once whether it is llama-server
        var body = Assert.Single(server.Bodies, b => b.Contains("\"prompt\"", StringComparison.Ordinal));
        Assert.Contains("\"suffix\"", body, StringComparison.Ordinal);
        Assert.Equal(0, Served(server, "/infill"));
    }

    [Fact]
    public async Task ARejectedRequest_IsSaidOnce_AndThatModelIsNotAskedAgain()
    {
        const string model = "refusing-chat-model";
        using var server = new LoopbackHttpServer(
            p => p.StartsWith("/v1/completions", StringComparison.Ordinal)
                ? "{\"error\":{\"message\":\"suffix is not currently supported\"}}"
                : null,
            _ => 400);
        var client = Client(server);

        for (var i = 0; i < 3; i++) Assert.Equal("", await CompleteAsync(client, model));

        Assert.Equal(1, Served(server, "/v1/completions"));
        Assert.True(client.InlineCompletionRefused(model));       // what /bench reads to leave it unscored
        var said = Diagnostics.Snapshot().Where(e => e.Detail.Contains(model, StringComparison.Ordinal)).ToList();
        Assert.Contains("suffix is not currently supported", Assert.Single(said).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusalOfOneModel_LeavesAnotherModelOfTheSameServerAsked()
    {
        using var server = new LoopbackHttpServer(
            (p, body) => p.StartsWith("/v1/completions", StringComparison.Ordinal)
                ? body.Contains("\"chat-only\"", StringComparison.Ordinal)
                    ? "{\"error\":{\"message\":\"chat-only does not support insert\"}}"
                    : "data: {\"choices\":[{\"text\":\"x\"}]}\n\ndata: [DONE]\n\n"
                : null,
            (_, body) => body.Contains("\"chat-only\"", StringComparison.Ordinal) ? 400 : 200);
        var client = Client(server);

        Assert.Equal("", await CompleteAsync(client, "chat-only"));
        Assert.Equal("x", await CompleteAsync(client, "coder-model"));
        Assert.Equal("x", await CompleteAsync(client, "coder-model"));

        Assert.Equal(3, Served(server, "/v1/completions"));
    }

    /// <summary>A refusal is held for a while, not for the process: a 400 can belong to one request.</summary>
    [Fact]
    public async Task ARefusedModel_IsAskedAgain_OnceItsHoldIsOver()
    {
        using var server = new LoopbackHttpServer(
            p => p.StartsWith("/v1/completions", StringComparison.Ordinal)
                ? "{\"error\":{\"message\":\"the request exceeds the available context size\"}}"
                : null,
            _ => 400);
        var client = Client(server);
        client.FimRefusalHold = TimeSpan.Zero;

        await CompleteAsync(client, "held-model");
        await CompleteAsync(client, "held-model");

        Assert.Equal(2, Served(server, "/v1/completions"));
    }

    /// <summary>llama-server answers 503 while it loads a model: that is not "not llama-server". Nothing is sent while the
    /// server does not say what it is (llama-server refuses <c>suffix</c>), and it is asked again.</summary>
    [Fact]
    public async Task ALlamaServerStillLoading_IsRecognizedOnceItAnswers()
    {
        var loading = true;
        using var server = new LoopbackHttpServer(
            p => p.StartsWith("/props", StringComparison.Ordinal)  ? (loading ? "{\"error\":{\"message\":\"Loading model\"}}" : "{\"default_generation_settings\":{}}")
               : p.StartsWith("/infill", StringComparison.Ordinal) ? "data: {\"content\":\"y\",\"stop\":true}\n\n"
               : p.StartsWith("/v1/completions", StringComparison.Ordinal) ? "data: {\"choices\":[{\"text\":\"x\"}]}\n\ndata: [DONE]\n\n"
               : null,
            p => p.StartsWith("/props", StringComparison.Ordinal) && loading ? 503 : 200);
        var client = Client(server);

        Assert.Equal("", await CompleteAsync(client, "loading-model"));
        Assert.Equal(0, Served(server, "/v1/completions") + Served(server, "/infill"));
        loading = false;
        var text = await CompleteAsync(client, "loading-model");

        Assert.Equal("y", text);
        Assert.Equal(2, Served(server, "/props"));
        Assert.Equal(0, Served(server, "/v1/completions"));
    }

    /// <summary>A missing key can be fixed in the settings: it stays with the breaker, never remembered as a refusal.</summary>
    [Fact]
    public async Task AnUnauthorizedRequest_IsAskedAgain()
    {
        using var server = new LoopbackHttpServer(
            p => p.StartsWith("/v1/completions", StringComparison.Ordinal) ? "{\"error\":{\"message\":\"invalid api key\"}}" : null,
            _ => 401);
        var client = Client(server);

        await CompleteAsync(client, "keyed-model");
        await CompleteAsync(client, "keyed-model");

        Assert.Equal(2, Served(server, "/v1/completions"));
    }
}
