using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A known model is asked with the sampling its vendor recommends — and only what the vendor publishes.
//
//  Inferpal sent no sampling at all, so the server's settings applied — and a server applies its generic defaults to
//  a model it has no preset for. LM Studio's include a repeat penalty of 1.1 and a min_p of 0.05; GLM 4.7's vendor
//  says to disable the first and set the second to 0.01. The family's values live in ModelProfiles; docs/models.md
//  says them, and ModelProfilesTests holds the two together.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class RecommendedSamplingTests
{
    private static string Sse(string content) =>
        "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content } } } }) + "\n\n"
        + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";

    private static LoopbackHttpServer OpenAiServer() =>
        new(path => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? Sse("Done.") : null);

    /// <summary>The body of the chat request the server received.</summary>
    private static JsonElement ChatBody(LoopbackHttpServer server, string marker = "\"messages\"") =>
        JsonDocument.Parse(Assert.Single(server.Bodies, b => b.Contains(marker, StringComparison.Ordinal))).RootElement.Clone();

    private static async Task<JsonElement> SendThroughLmStudioAsync(string model, bool recommended = true)
    {
        using var server = OpenAiServer();
        var client = new LmStudioClient(new InferpalConfig
            { Provider = "lmstudio", BaseUrl = server.BaseUrl, UseRecommendedSampling = recommended });
        await client.SendChatAsync(model, [new ChatMessageDto("user", "x")], EmptyToolRegistry.Instance, null, CancellationToken.None);
        return ChatBody(server);
    }

    [Fact]
    public async Task LmStudio_GetsEveryValueTheVendorPublishes()
    {
        var body = await SendThroughLmStudioAsync("zai-org/glm-4.7-flash");

        Assert.Equal(0.7,  body.GetProperty("temperature").GetDouble());
        Assert.Equal(1.0,  body.GetProperty("top_p").GetDouble());
        Assert.Equal(0.01, body.GetProperty("min_p").GetDouble());
        Assert.Equal(1.0,  body.GetProperty("repeat_penalty").GetDouble());
        Assert.False(body.TryGetProperty("top_k", out _));            // Z.ai publishes none: the server's applies
    }

    [Fact]
    public async Task AValueTheVendorDoesNotPublish_IsNotSent()
    {
        var body = await SendThroughLmStudioAsync("mistralai/devstral-small-2-2512");

        Assert.Equal(0.15, body.GetProperty("temperature").GetDouble());
        Assert.False(body.TryGetProperty("top_p", out _));
        Assert.False(body.TryGetProperty("top_k", out _));
        Assert.False(body.TryGetProperty("repeat_penalty", out _));
    }

    [Theory]
    [InlineData("some-unknown-model")]                               // a family Inferpal does not know
    public async Task AnUnknownModel_GetsNoSampling(string model)
    {
        var body = await SendThroughLmStudioAsync(model);

        Assert.True(body.TryGetProperty("messages", out _));             // witness: this is the chat request
        Assert.False(body.TryGetProperty("temperature", out _));
        Assert.False(body.TryGetProperty("top_p", out _));
    }

    [Fact]
    public async Task TurnedOff_NothingIsSent_AndTheServersSettingsApply()
    {
        var body = await SendThroughLmStudioAsync("google/gemma-4-31b-qat", recommended: false);

        Assert.True(body.TryGetProperty("messages", out _));
        Assert.False(body.TryGetProperty("temperature", out _));
        Assert.False(body.TryGetProperty("top_k", out _));
    }

    [Fact]
    public async Task AGenericOpenAiServer_GetsOnlyTheFieldsTheApiDefines()
    {
        // A strict server refuses a request carrying a field it does not know: top_k, min_p and repeat_penalty are
        // LM Studio / llama.cpp extensions.
        using var server = OpenAiServer();
        var client = new OpenAiCompatibleClient(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl });

        await client.SendChatAsync("glm-4.7-flash", [new ChatMessageDto("user", "x")], EmptyToolRegistry.Instance, null, CancellationToken.None);

        var body = ChatBody(server);
        Assert.Equal(0.7, body.GetProperty("temperature").GetDouble());
        Assert.Equal(1.0, body.GetProperty("top_p").GetDouble());
        Assert.False(body.TryGetProperty("min_p", out _));
        Assert.False(body.TryGetProperty("repeat_penalty", out _));
        Assert.False(body.TryGetProperty("top_k", out _));
    }

    [Fact]
    public async Task Ollama_GetsThemInItsOptions_BesideTheContextWindow()
    {
        var reply = JsonSerializer.Serialize(new { message = new { role = "assistant", content = "Done." }, done = true, done_reason = "stop" }) + "\n";
        using var server = new LoopbackHttpServer(path => path.StartsWith("/api/chat", StringComparison.Ordinal) ? reply : null);
        var client = new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl, ContextWindowSize = 16384 });

        await client.SendChatAsync("gemma4:31b", [new ChatMessageDto("user", "x")], EmptyToolRegistry.Instance, null, CancellationToken.None);

        var options = ChatBody(server).GetProperty("options");
        Assert.Equal(16384, options.GetProperty("num_ctx").GetInt32());
        Assert.Equal(1.0,  options.GetProperty("temperature").GetDouble());
        Assert.Equal(0.95, options.GetProperty("top_p").GetDouble());
        Assert.Equal(64,   options.GetProperty("top_k").GetInt32());
    }
}
