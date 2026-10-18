using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Execution;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Ollama loads the configured window and, handed more, cuts the head of the request — the system prompt first — and
/// answers anyway: a request the estimate already overflows is refused before it is sent, with what it is made of.
/// </summary>
[Collection(SlowServerCollection.Name)]
public sealed class OllamaRequestFitTests
{
    private readonly string _model = $"m-{Guid.NewGuid():N}";

    private static readonly List<ChatMessageDto> LongQuestion =
        [new("user", "Summarize this log:\n" + string.Concat(Enumerable.Range(0, 1_600).Select(i => $"[{i:0000}] batch {i * 7} done in {i % 13} ms\n")))];

    private static string Done() =>
        "{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"done\":false}\n"
        + "{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true,\"prompt_eval_count\":10,\"eval_count\":1}\n";

    [Fact]
    public async Task ARequestOverTheConfiguredWindow_IsRefusedBeforeItIsSent_WithWhatItIsMadeOf()
    {
        using var server = new SlowReadServer(headersFirst: false, TimeSpan.FromSeconds(1), "application/x-ndjson", Done());
        var client = new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl, ContextWindowSize = 4_096 });

        var ex = await Assert.ThrowsAsync<AgentHttpException>(() => client.SendChatAsync(
            _model, new List<ChatMessageDto>(LongQuestion), EmptyToolRegistry.Instance, null, CancellationToken.None));

        var size = RequestSize.Of(LongQuestion, null);
        Assert.Equal(Strings.MsgContextWontFit(size.Total, 4_096, size.Breakdown()), ex.Message);
    }

    /// <summary>Reference arm: a window left to the server (0) is unknown, so the same request is sent and answered.</summary>
    [Fact]
    public async Task AWindowLeftToTheServer_RefusesNothing()
    {
        using var server = new SlowReadServer(headersFirst: false, TimeSpan.FromSeconds(1), "application/x-ndjson", Done());
        var client = new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl, ContextWindowSize = 0 });

        var turn = await client.SendChatAsync(_model, new List<ChatMessageDto>(LongQuestion), EmptyToolRegistry.Instance, null,
                                              CancellationToken.None);

        Assert.Equal("ok", turn.TextContent);
    }
}
