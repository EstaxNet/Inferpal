using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An intra-run summary that cannot bring the run under the target is not asked for.
/// </summary>
/// <remarks>
/// ⚠ At the default 8K window, the system prompt and the built-in tool definitions alone fill the 70 % target: the first
/// tool result crossed the 80 % threshold, the run paid a whole extra inference call for a summary of almost nothing,
/// and elided anyway. Measured on the bench at 8K: without that call, Devstral took a median 24.8 s a task instead of
/// 36.4 s, the same tasks passing.
/// </remarks>
public class SummaryThatCannotFitTests
{
    private sealed class CountingClient : IOllamaChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatTurnResult> SendChatAsync(
            string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal, string? toolChoice = null,
            Action<string>? onThinking = null)
        {
            Calls++;
            return Task.FromResult(new ChatTurnResult("SUMMARY OF OLD TURNS", null, 0, 0));
        }
    }

    // Head (system + user) anchored at index 2, then old tool results that overflow the window.
    private static List<ChatMessageDto> Run() =>
        new List<ChatMessageDto> { new("system", new string('s', 4000)), new("user", "go") }
            .Concat(Enumerable.Range(0, 8).Select(_ => new ChatMessageDto("tool", new string('x', 4000))))
            .ToList();

    private static async Task<(int Calls, List<ChatMessageDto> Messages)> CompactAsync(int toolTokens)
    {
        var client   = new CountingClient();
        var orch     = new AgentOrchestrator(client, new InferpalConfig { ContextWindowSize = 8192, CompactionEnabled = true });
        var messages = Run();
        await orch.CompactRunContextAsync(messages, anchorCount: 2, model: "m", alreadySummarized: false,
                                          onStep: _ => { }, ct: CancellationToken.None, toolTokens: toolTokens);
        return (client.Calls, messages);
    }

    [Fact]
    public async Task WhenTheHeadAndTheToolsFillTheTarget_NoSummaryIsAskedFor_AndTheRunIsElided()
    {
        // ~1 000 tokens of head + 4 900 of tool definitions: over 70 % of 8 192 before any tool result.
        var (calls, messages) = await CompactAsync(toolTokens: 4900);

        Assert.Equal(0, calls);
        Assert.Contains(messages, m => m.Role == "tool" && m.Content!.Length < 4000);   // elided instead
    }

    /// <summary>⚠ Reference arm: with room to spare, the summary is asked for as before.</summary>
    [Fact]
    public async Task WhenTheRunCanFit_TheSummaryIsAskedFor()
    {
        var (calls, _) = await CompactAsync(toolTokens: 0);

        Assert.Equal(1, calls);
    }
}
