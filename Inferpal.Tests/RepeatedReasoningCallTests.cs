using System.Text;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A tool call written twice in the reasoning channel stops the response at the repeat.
//
//  OutputBound makes a looping response end; it does not make it end soon. Measured on LM Studio with
//  Qwen3-4B-Thinking: the bound stopped the loop after 6 min 30 to 7 min 40 of "Thinking…" — over a
//  call the model had written in its first seconds and was repeating identically. The client now stops
//  at the first identical repeat and runs that call. Distinct calls, and a single drafted call, are left
//  alone: that is the case the reasoning-channel recovery exists for.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class RepeatedReasoningCallTests
{
    private const string Diagnostics = "<tool_call> {\"name\": \"get_diagnostics\", \"arguments\": {}} </tool_call> ";
    private const string ReadCounter = "<tool_call> {\"name\": \"read_file\", \"arguments\": {\"path\": \"Counter.razor\"}} </tool_call> ";

    private static bool Feed(RepeatedCallDetector detector, StringBuilder reasoning, string text, int chunk)
    {
        for (var i = 0; i < text.Length; i += chunk)
        {
            reasoning.Append(text, i, Math.Min(chunk, text.Length - i));
            if (detector.Repeats(reasoning)) return true;
        }
        return false;
    }

    [Theory]
    [InlineData(1)]    // a closing tag split over many deltas
    [InlineData(7)]
    [InlineData(500)]  // several calls in one delta
    public void TheSameCallTwice_IsARepeat_WhateverTheChunking(int chunk)
    {
        var detector  = new RepeatedCallDetector();
        var reasoning = new StringBuilder("I should check the build first. ");

        Assert.False(Feed(detector, reasoning, Diagnostics, chunk));
        Assert.True(Feed(detector, reasoning, "Let me do it. " + Diagnostics, chunk));
    }

    [Fact]
    public void DistinctCalls_AreNotARepeat_UntilOneComesBack()
    {
        var detector  = new RepeatedCallDetector();
        var reasoning = new StringBuilder();

        Assert.False(Feed(detector, reasoning, ReadCounter + Diagnostics, 13));
        Assert.True(Feed(detector, reasoning, ReadCounter, 13));
    }

    // ── The production client, a real socket ───────────────────────────────

    private sealed class Tools : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
            [new("function", new ToolFunction("get_diagnostics", "build", new { })),
             new("function", new ToolFunction("read_file", "read", new { }))];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => Task.FromResult("ok");
    }

    private static string ReasoningStream(IEnumerable<string> deltas)
    {
        var sb = new StringBuilder();
        foreach (var d in deltas)
            sb.Append("data: ")
              .Append(JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { reasoning_content = d } } } }))
              .Append("\n\n");
        return sb.Append("data: [DONE]\n\n").ToString();
    }

    private static async Task<ChatTurnResult> SendAsync(string body)
    {
        using var server = new LoopbackHttpServer(path => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? body : null);
        // A window large enough that OutputBound never plays: only the repeat can stop this stream.
        var client = new OpenAiCompatibleClient(new InferpalConfig
        {
            Provider = "openai-compatible", BaseUrl = server.BaseUrl, ContextWindowSize = 262_144,
        });
        var result = await client.SendChatAsync("m", [new ChatMessageDto("user", "fix it")], new Tools(), onToken: null,
                                                CancellationToken.None);
        Assert.Contains(server.Paths, p => p.StartsWith("/v1/chat/completions", StringComparison.Ordinal));   // witness
        return result;
    }

    [Fact]
    public async Task ACallLoopingInTheReasoning_IsStoppedAtTheRepeat_AndRunOnce()
    {
        var result = await SendAsync(ReasoningStream(Enumerable.Repeat(Diagnostics, 300)));

        var call = Assert.Single(result.ToolCalls!);
        Assert.Equal("get_diagnostics", call.Function.Name);
        Assert.False(result.CutAtLimit);   // a repeat is a decision, not a cut
    }

    [Fact]
    public async Task TwoDistinctCallsInTheReasoning_AreBothKept()
    {
        // Reference arm: nothing repeats, so nothing is stopped or dropped.
        var result = await SendAsync(ReasoningStream([ReadCounter, Diagnostics]));

        Assert.Equal(["read_file", "get_diagnostics"], result.ToolCalls!.Select(c => c.Function.Name));
    }
}
