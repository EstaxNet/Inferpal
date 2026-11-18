using System.Globalization;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Inference;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  The speed each answer's header shows ("21 s · 38 tok/s"): what the SERVER counted for the model that answered, over
//  the time it spent writing — never the chunks counted instead, never another model of the turn, never "0 tok/s".
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class GenerationMeterTests
{
    [Fact]
    public void TheSpeed_IsTheAnsweringModels_OverEveryCallItAnswered()
    {
        using var turn = GenerationMeter.Open();
        GenerationMeter.Generated("big", 300, TimeSpan.FromSeconds(10));
        GenerationMeter.Generated("big", 100, TimeSpan.FromSeconds(2));
        GenerationMeter.Generated("utility", 50, TimeSpan.FromSeconds(1));   // a title by another model: not in it
        GenerationMeter.Generated("big", 0, TimeSpan.FromSeconds(5));        // no count: no sample
        GenerationMeter.Generated("big", 80, TimeSpan.Zero);                 // no time: no sample

        Assert.Equal(new GenerationRate(400, TimeSpan.FromSeconds(12)), turn.RateOf("big"));
        Assert.Null(turn.RateOf("nobody"));
    }

    [Fact]
    public void NothingIsCounted_OutsideAnOpenTurn()
    {
        GenerationMeter.Generated("big", 300, TimeSpan.FromSeconds(10));     // no scope: nowhere to go
        var turn = GenerationMeter.Open();
        turn.Dispose();
        GenerationMeter.Generated("big", 300, TimeSpan.FromSeconds(10));     // work that outlives the turn

        Assert.Null(turn.RateOf("big"));
    }

    [Fact]
    public void TheHeader_SaysTheSpeedAfterTheDuration_AndNeverZero()
    {
        var elapsed = TimeSpan.FromSeconds(21);
        var fast    = new GenerationRate(1240, TimeSpan.FromSeconds(32.6));
        var slow    = new GenerationRate(42, TimeSpan.FromSeconds(10));

        Assert.Equal(RunSummary.Duration(elapsed) + " · " + Strings.TurnRate("38"), RunSummary.Timing(elapsed, fast));
        Assert.Equal(Strings.TurnRate(4.2.ToString("0.0", CultureInfo.CurrentCulture)), RunSummary.Rate(slow));
        Assert.Equal(RunSummary.Duration(elapsed), RunSummary.Timing(elapsed, null));
        Assert.Null(RunSummary.Rate(null));
        Assert.Equal(Strings.TurnRateDetail(1240, 32.6), RunSummary.RateDetail(fast));
        Assert.Null(RunSummary.RateDetail(null));
    }

    // ── Through the clients ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ollama_CountsWhatTheServerMeasured()
    {
        // 120 tokens in 3 s by Ollama's own count, whatever the loopback's clock says.
        var done = JsonSerializer.Serialize(new { message = new { role = "assistant", content = "" }, done = true,
                                                  done_reason = "stop", prompt_eval_count = 10, eval_count = 120,
                                                  eval_duration = 3_000_000_000L });
        using var server = new LoopbackHttpServer(path => !path.StartsWith("/api/chat", StringComparison.Ordinal) ? null
            : JsonSerializer.Serialize(new { message = new { role = "assistant", content = "Hello" }, done = false }) + "\n" + done + "\n");
        var client = new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl, ContextWindowSize = 8192 });
        using var turn = GenerationMeter.Open();

        var answer = await client.SendChatAsync("small", [new ChatMessageDto("user", "hi")], EmptyToolRegistry.Instance,
                                                onToken: null, CancellationToken.None);

        Assert.Equal("Hello", answer.TextContent);
        Assert.Equal(new GenerationRate(120, TimeSpan.FromSeconds(3)), turn.RateOf("small"));
    }

    [Fact]
    public async Task AStreamWithoutTheServersCount_ShowsNoSpeed()
    {
        // An OpenAI-compatible stream that ends without its usage chunk: no figure, never the chunks counted instead.
        using var server = new LoopbackHttpServer(path => !path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? null
            : "data: {\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hello\"}}]}\n\n"
              + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
              + "data: [DONE]\n\n");
        var client = new OpenAiCompatibleClient(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl });
        using var turn = GenerationMeter.Open();

        var answer = await client.SendChatAsync("small", [new ChatMessageDto("user", "hi")], EmptyToolRegistry.Instance,
                                                onToken: null, CancellationToken.None);

        Assert.Equal("Hello", answer.TextContent);   // witness: the stream was read
        Assert.Null(turn.RateOf("small"));
    }

    private static LoopbackHttpServer Streaming(string chunks) =>
        new(path => !path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? null : chunks + "data: [DONE]\n\n");

    private static string Content(string text) =>
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" + text + "\"}}]}\n\n";

    private static async Task<GenerationRate?> RateOfAsync(LoopbackHttpServer server)
    {
        var client = new OpenAiCompatibleClient(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl });
        using var turn = GenerationMeter.Open();
        var answer = await client.SendChatAsync("small", [new ChatMessageDto("user", "hi")], EmptyToolRegistry.Instance,
                                                onToken: null, CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(answer.TextContent));   // witness: the stream was read
        return turn.RateOf("small");
    }

    [Fact]
    public async Task LlamaServer_CountsWhatItMeasuredItself()
    {
        var rate = await RateOfAsync(Streaming(Content("Hello") + Content(" there")
            + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],"
            + "\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":120,\"total_tokens\":130},"
            + "\"timings\":{\"predicted_n\":120,\"predicted_ms\":3000.0}}\n\n"));

        Assert.Equal(new GenerationRate(120, TimeSpan.FromSeconds(3)), rate);
    }

    [Fact]
    public async Task AStreamedAnswer_CountsTheTokensWrittenWhileTheClockRan()
    {
        // 40 tokens in 4 chunks: the clock starts at the first, so its quarter of the count is left out.
        var rate = await RateOfAsync(Streaming(Content("a") + Content("b") + Content("c") + Content("d")
            + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],"
            + "\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":40,\"total_tokens\":50}}\n\n"));

        Assert.NotNull(rate);
        Assert.Equal(30, rate.Value.Tokens);
        Assert.True(rate.Value.Time > TimeSpan.Zero);
    }

    [Fact]
    public async Task AnAnswerSentInOneChunk_ShowsNoSpeed()
    {
        // A tool call sent once complete (Ollama's /v1): 80 tokens over the milliseconds after the chunk is no speed.
        using var server = new LoopbackHttpServer(path => !path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? null
            : "data: {\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"type\":\"function\","
              + "\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"a.cs\\\"}\"}}]}}]}\n\n"
              + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}],"
              + "\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":80,\"total_tokens\":90}}\n\n"
              + "data: [DONE]\n\n");
        var client = new OpenAiCompatibleClient(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl });
        using var turn = GenerationMeter.Open();

        var answer = await client.SendChatAsync("small", [new ChatMessageDto("user", "read a.cs")], EmptyToolRegistry.Instance,
                                                onToken: null, CancellationToken.None);

        Assert.Equal("read_file", Assert.Single(answer.ToolCalls!).Function.Name);   // witness: the call was read
        Assert.Null(turn.RateOf("small"));
    }
}
