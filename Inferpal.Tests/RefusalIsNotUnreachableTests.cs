using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ The chat's circuit breaker guards REACHABILITY — its own doctrine says so: success is recorded as soon as the response
/// headers arrive. A server that answers 4xx has answered: it refused THIS request (a context the request overflows, a
/// model name it does not have, a parameter it rejects). Counted as failures, five such refusals in a row — the user
/// retrying the question that is too long — locked the chat for five minutes under "circuit breaker open… after repeated
/// failures", even once the request was fixed: the very remedy the refusal named stopped working.
/// </summary>
public class RefusalIsNotUnreachableTests
{
    private static List<ChatMessageDto> Question => [new ChatMessageDto("user", "hi")];

    private static async Task<List<string>> SixRefusedAsync(IInferenceProvider client)
    {
        var messages = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var ex = await Assert.ThrowsAsync<AgentHttpException>(() =>
                client.SendChatAsync("m", Question, EmptyToolRegistry.Instance, onToken: null, CancellationToken.None));
            messages.Add(ex.Message);
        }
        return messages;
    }

    [Fact]
    public async Task Ollama_SixRefusedRequests_AreSixAnswers_NotALockout()
    {
        using var server = new LoopbackHttpServer(
            path => path.StartsWith("/api/chat", StringComparison.Ordinal)
                ? "{\"error\":\"model \\\"m\\\" not found, try pulling it first\"}" : null,
            _ => 404);
        var client = new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl });

        var messages = await SixRefusedAsync(client);

        Assert.Equal(6, server.Paths.Count(p => p.StartsWith("/api/chat", StringComparison.Ordinal)));   // all reached it
        Assert.All(messages, m => Assert.Contains("not found", m));
        Assert.DoesNotContain(messages, m => m.Contains(Strings.MsgCircuitOpen, StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenAiCompatible_SixContextOverflows_AreSixAnswers_NotALockout()
    {
        using var server = new LoopbackHttpServer(
            path => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal)
                ? "{\"error\":\"request (9000 tokens) exceeds the available context size (8192 tokens)\"}" : null,
            _ => 400);
        var client = new OpenAiCompatibleClient(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl });

        var messages = await SixRefusedAsync(client);

        Assert.Equal(6, server.Paths.Count(p => p.StartsWith("/v1/chat/completions", StringComparison.Ordinal)));
        Assert.DoesNotContain(messages, m => m.Contains(Strings.MsgCircuitOpen, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AServerThatFails_StillOpensTheBreaker()
    {
        // Reference arm: a server that fails on its side (5xx) five times in a row is still left alone for a while.
        using var server = new LoopbackHttpServer(
            path => path.StartsWith("/api/chat", StringComparison.Ordinal) ? "{\"error\":\"model runner has unexpectedly stopped\"}" : null,
            _ => 500);
        var client = new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl });

        var messages = await SixRefusedAsync(client);

        Assert.Equal(5, server.Paths.Count(p => p.StartsWith("/api/chat", StringComparison.Ordinal)));
        Assert.Contains(Strings.MsgCircuitOpen, messages[5]);
    }
}
