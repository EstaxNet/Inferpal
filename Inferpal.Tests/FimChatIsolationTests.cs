using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Inference;
using Inferpal.Services.Signals;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Inline completion and the chat share the client in VS Code (<c>fim/complete</c> calls the session's client), and with
/// the default settings the completion model IS the chat model. Two faults lived there.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Ollama refuses <c>/api/generate</c> with a <c>suffix</c> for a model whose template has no insert slot — most chat
/// models — with HTTP 400 "… does not support insert", before the model loads (server/routes.go, GenerateHandler →
/// scheduleRunner). The completion swallowed it without a word: no ghost text, ever, and no trace of why.
/// </para>
/// <para>
/// ⚠ And it counted against the CHAT's circuit breaker: five pauses in typing later the breaker was open for five
/// minutes, and the next question failed with "circuit breaker open" although the chat had never failed. The
/// embedding breaker is separate for exactly that reason; the completion's was not.
/// </para>
/// </remarks>
[Collection(SignalCollection.Name)]
public sealed class FimChatIsolationTests : IDisposable
{
    // Ghost text yields to a chat turn marked busy in the signal folder: never the real one.
    private readonly SignalScratchDir _signals = new();

    public void Dispose() => _signals.Dispose();

    private static string ChatAnswer(string text) =>
        JsonSerializer.Serialize(new { message = new { role = "assistant", content = text }, done = false }) + "\n"
        + JsonSerializer.Serialize(new { message = new { role = "assistant", content = "" }, done = true, done_reason = "stop" }) + "\n";

    private static string Generated(string text) =>
        JsonSerializer.Serialize(new { response = text, done = false }) + "\n"
        + JsonSerializer.Serialize(new { response = "", done = true }) + "\n";

    private static Task CompleteAsync(InferenceProviderBase client, string model, Action<string>? onToken = null) =>
        client.StreamFimAsync("int Add(int a, int b) {\n    ", "\n}", 16, 0.2, onToken ?? (_ => { }), CancellationToken.None, model);

    [Fact]
    public async Task Ollama_RefusedCompletions_NeverCloseTheChat()
    {
        using var server = new LoopbackHttpServer(
            path => path.StartsWith("/api/generate", StringComparison.Ordinal) ? "{\"error\":\"model runner has unexpectedly stopped\"}"
                  : path.StartsWith("/api/chat", StringComparison.Ordinal)     ? ChatAnswer("Hello.")
                  : null,
            path => path.StartsWith("/api/generate", StringComparison.Ordinal) ? 500 : 200);
        var client = new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl });

        for (var i = 0; i < 8; i++) await CompleteAsync(client, "m");
        Assert.True(server.Paths.Count(p => p.StartsWith("/api/generate", StringComparison.Ordinal)) >= 5);   // witness

        var turn = await client.SendChatAsync("m", [new ChatMessageDto("user", "hi")], EmptyToolRegistry.Instance,
                                              onToken: null, CancellationToken.None);

        Assert.Equal("Hello.", turn.TextContent);
    }

    [Fact]
    public async Task LmStudio_RefusedCompletions_NeverCloseTheChat()
    {
        var sse = "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content = "Hello." } } } }) + "\n\n"
                  + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        using var server = new LoopbackHttpServer(
            path => path.StartsWith("/v1/completions", StringComparison.Ordinal)
                        ? "data: {\"error\":{\"message\":\"model does not support completions\"}}\n\ndata: [DONE]\n\n"
                  : path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? sse
                  : null);
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl });

        for (var i = 0; i < 8; i++) await CompleteAsync(client, "m");
        Assert.True(server.Paths.Count(p => p.StartsWith("/v1/completions", StringComparison.Ordinal)) >= 5);   // witness

        var turn = await client.SendChatAsync("m", [new ChatMessageDto("user", "hi")], EmptyToolRegistry.Instance,
                                              onToken: null, CancellationToken.None);

        Assert.Equal("Hello.", turn.TextContent);
    }

    [Fact]
    public async Task Ollama_RefusedCompletions_StillStopBeingSent()
    {
        // The completion keeps a breaker of its own: a server that refuses every one is not asked on every keystroke.
        using var server = new LoopbackHttpServer(
            path => path.StartsWith("/api/generate", StringComparison.Ordinal) ? "{\"error\":\"model runner has unexpectedly stopped\"}" : null,
            _ => 500);
        var client = new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl });

        for (var i = 0; i < 20; i++) await CompleteAsync(client, "m");

        Assert.InRange(server.Paths.Count(p => p.StartsWith("/api/generate", StringComparison.Ordinal)), 1, 5);
    }

    /// <summary>A chat model on Ollama: refused with a suffix, served without one.</summary>
    private static LoopbackHttpServer ChatModelOnOllama(string model, string completion) => new(
        (path, request) => !path.StartsWith("/api/generate", StringComparison.Ordinal) ? null
                         : request.Contains("\"suffix\"", StringComparison.Ordinal)
                             ? JsonSerializer.Serialize(new { error = $"registry.ollama.ai/library/{model}:latest does not support insert" })
                             : Generated(completion),
        (_, request) => request.Contains("\"suffix\"", StringComparison.Ordinal) ? 400 : 200);

    [Fact]
    public async Task Ollama_AModelWithoutInsert_IsCompletedFromThePrompt_AndTheRefusalIsSaidOnce()
    {
        var model = "qwen3-" + Guid.NewGuid().ToString("N")[..8];
        using var server = ChatModelOnOllama(model, "return a + b;");
        var client = new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl });
        var got = new System.Text.StringBuilder();

        await CompleteAsync(client, model, t => got.Append(t));
        await CompleteAsync(client, model);

        Assert.Equal("return a + b;", got.ToString());
        var generates = server.Bodies.Where(b => b.Contains("\"prompt\"", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, generates.Count);                                   // refused once, then the prompt twice
        Assert.Contains("\"raw\":true", generates[1]);
        Assert.DoesNotContain("\"suffix\"", generates[2]);
        Assert.Single(Diagnostics.Snapshot(), e => e.Detail.Contains(model, StringComparison.Ordinal)
                                                 && e.Detail.Contains("does not support insert", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ollama_AModelWithInsert_KeepsItsNativeFillInTheMiddle()
    {
        // Reference arm: a model whose template has the insert slot is sent the suffix, as before.
        var model = "qwen2.5-coder-" + Guid.NewGuid().ToString("N")[..8];
        using var server = new LoopbackHttpServer(
            path => path.StartsWith("/api/generate", StringComparison.Ordinal) ? Generated("return a + b;") : null);
        var client = new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl });

        await CompleteAsync(client, model);

        var request = Assert.Single(server.Bodies.Where(b => b.Contains("\"prompt\"", StringComparison.Ordinal)));
        Assert.Contains("\"suffix\"", request);
        Assert.DoesNotContain("\"raw\"", request);
    }
}
