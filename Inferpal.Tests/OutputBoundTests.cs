using System.Text;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A response that never ends is stopped by the client.
//
//  No chat request carries an output limit, and each client's deadline is an inactivity deadline
//  re-armed by every chunk. Measured on LM Studio with Qwen3-4B-Thinking (the model a Visual Studio
//  Magazine test used): after its plan, the model wrote the same `<tool_call>` over and over inside a
//  reasoning block it never closed, for the full 25 minutes of each run — zero tools executed, "Thinking…"
//  on screen, and the recovery of a call written in the reasoning channel, which runs when the stream
//  ends, never ran. The client now stops reading at the room the window leaves, says the response was
//  cut, and keeps the first call the model wrote — the rest is the loop.
//
//  The stand-in server serves a long but FINITE loop: without the bound the client reads all of it,
//  recovers hundreds of calls and calls the response complete — the discriminator. Its calls DIFFER
//  from one another, as a decaying loop's do: an identical repeat is stopped earlier, at the repeat
//  (RepeatedReasoningCallTests), and would hide the bound.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class OutputBoundTests
{
    private sealed class Tools : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
            [new("function", new ToolFunction("get_diagnostics", "build", new { }))];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => Task.FromResult("ok");
    }

    private const string LoopedCall = "<tool_call> {\"name\": \"get_diagnostics\", \"arguments\": {}} </tool_call> ";
    private const int    Repeats    = 2000;   // ~140 000 characters: far past a 2 048-token window

    [Fact]
    public void TheBound_IsTheRoomTheWindowLeaves_NeverLessThanAFloor()
    {
        Assert.Equal((8192L - 2000) * OutputBound.CharsPerToken, OutputBound.MaxChars(8192, 2000));
        Assert.Equal((long)OutputBound.MinTokens * OutputBound.CharsPerToken, OutputBound.MaxChars(8192, 9000));
        Assert.Equal((long)OutputBound.UnknownWindow * OutputBound.CharsPerToken, OutputBound.MaxChars(0, 0));
    }

    // ── OpenAI-compatible (LM Studio, vLLM, llama-server) ─────────────────────

    private static string ReasoningLoopStream()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < Repeats; i++)
        {
            var call = $"<tool_call> {{\"name\": \"get_diagnostics\", \"arguments\": {{\"attempt\": {i}}}}} </tool_call> ";
            sb.Append("data: ")
              .Append(JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { reasoning_content = call } } } }))
              .Append("\n\n");
        }
        return sb.Append("data: [DONE]\n\n").ToString();
    }

    private static string AnswerStream(string text) =>
        "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content = text } } } }) + "\n\n"
        + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
        + "data: [DONE]\n\n";

    private static Task<ChatTurnResult> SendAsync(string body) =>
        SendAsync(new LoopbackHttpServer(path => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? body : null));

    private static async Task<ChatTurnResult> SendAsync(LoopbackHttpServer server)
    {
        using (server)
        {
            var client = new OpenAiCompatibleClient(new InferpalConfig
            {
                Provider = "openai-compatible", BaseUrl = server.BaseUrl, ContextWindowSize = 2048,
            });
            var result = await client.SendChatAsync("m", [new ChatMessageDto("user", "fix it")], new Tools(), onToken: null,
                                                    CancellationToken.None);
            Assert.Contains(server.Paths, p => p.StartsWith("/v1/chat/completions", StringComparison.Ordinal));   // witness
            return result;
        }
    }

    [Fact]
    public async Task AReasoningLoop_IsStopped_SaidToBeCut_AndKeepsTheFirstCallOnly()
    {
        var result = await SendAsync(ReasoningLoopStream());

        Assert.True(result.CutAtLimit);
        var call = Assert.Single(result.ToolCalls!);
        Assert.Equal("get_diagnostics", call.Function.Name);
    }

    [Fact]
    public async Task AnAnswerThatFits_IsNeitherStoppedNorCut()
    {
        // Reference arm: 6 000 characters of answer, under the ~10 000 a 2 048-token window leaves.
        var text   = new string('a', 6000);
        var result = await SendAsync(AnswerStream(text));

        Assert.False(result.CutAtLimit);
        Assert.Equal(text, result.TextContent);
    }

    // ── Ollama ────────────────────────────────────────────────────────────────

    private static string OllamaThinkingLoop()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < Repeats; i++)
            sb.Append(JsonSerializer.Serialize(new { message = new { role = "assistant", content = "", thinking = LoopedCall }, done = false }))
              .Append('\n');
        return sb.ToString();
    }

    private static string OllamaAnswer(string text) =>
        JsonSerializer.Serialize(new { message = new { role = "assistant", content = text }, done = false }) + "\n"
        + JsonSerializer.Serialize(new { message = new { role = "assistant", content = "" }, done = true, done_reason = "stop" }) + "\n";

    private static async Task<ChatTurnResult> SendOllamaAsync(string body)
    {
        using var server = new LoopbackHttpServer(path => path.StartsWith("/api/chat", StringComparison.Ordinal) ? body : null);
        var client = new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl, ContextWindowSize = 2048 });
        var result = await client.SendChatAsync("m", [new ChatMessageDto("user", "fix it")], new Tools(), onToken: null,
                                                CancellationToken.None);
        Assert.Contains(server.Paths, p => p.StartsWith("/api/chat", StringComparison.Ordinal));   // witness
        return result;
    }

    [Fact]
    public async Task Ollama_AThinkingLoop_IsStopped_AndSaidToBeCut()
    {
        Assert.True((await SendOllamaAsync(OllamaThinkingLoop())).CutAtLimit);
    }

    [Fact]
    public async Task Ollama_AnAnswerThatFits_IsNotCut()
    {
        var text   = new string('a', 6000);
        var result = await SendOllamaAsync(OllamaAnswer(text));

        Assert.False(result.CutAtLimit);
        Assert.Equal(text, result.TextContent);
    }
}
