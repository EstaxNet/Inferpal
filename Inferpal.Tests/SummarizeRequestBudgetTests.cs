using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The request that asks for a summary must fit the summarizer's window. It carried the whole system prompt (pinned
/// files included) and every message to compact, unbounded — and the slice to compact is largest exactly when it is
/// needed most: a long conversation reopened (every turn the screen kept), or an agent run full of tool output.
/// LM Studio then refused it (compaction fell back to truncation); Ollama cut its head in silence, and the "summary"
/// covered only the end of what it claimed to summarise.
/// </summary>
[Collection(CultureSerialCollection.Name)]   // compares a localized sentence
public class SummarizeRequestBudgetTests
{
    private static List<ChatMessageDto> Conversation(int turns, int answerChars)
    {
        var h = new List<ChatMessageDto> { new("system", "SYSTEM PROMPT " + new string('s', 20_000)) };
        for (var i = 0; i < turns; i++)
        {
            h.Add(new ChatMessageDto("user", $"question {i}"));
            h.Add(new ChatMessageDto("assistant", $"answer {i} " + new string('a', answerChars)));
        }
        return h;
    }

    private static InferpalConfig Config() => new()
    {
        ContextWindowSize        = 8_192,
        ContextWindowKeepTurns   = 2,
        CompactionEnabled        = true,
        KvCacheAnchorMessages    = 0,
        CompactionTimeoutSeconds = 10,
    };

    private static async Task<(ContextDecision Decision, int RequestChars, string Request)> Compact(List<ChatMessageDto> history)
    {
        var requestChars = 0;
        var request      = string.Empty;
        var client = new FakeInferenceProvider
        {
            OnChatRequest = (_, messages, _, _) =>
            {
                requestChars = messages.Sum(m => (m.Content ?? string.Empty).Length);
                request      = string.Concat(messages.Select(m => m.Content));
                return Task.FromResult(new ChatTurnResult("SUMMARY", null, 1, 1));
            },
        };
        var decision = await ContextManager.PrepareAsync(history, Config(), client, lastPromptTokens: 100_000,
                                                         onStep: null, CancellationToken.None);
        return (decision, requestChars, request);
    }

    [Fact]
    public async Task ASummaryRequest_FitsTheSummarizersWindow()
    {
        var (decision, chars, _) = await Compact(Conversation(turns: 30, answerChars: 3_000));   // ~90 000 characters

        Assert.Equal(ContextOutcome.Compacted, decision.Outcome);                               // witness
        Assert.True(chars <= 8_192 * 4, $"the summary request carried {chars} characters into an 8 192-token window");
    }

    [Fact]
    public async Task TheBudget_IsTheUtilityModelsLoadedWindow_NotTheConfiguredOne()
    {
        // The summarizer is the utility model, and a server may have loaded it with less than the configured window.
        var config = Config();
        config.ContextWindowSize = 32_768;
        config.UtilityModel      = "utility-small";
        var requestChars = 0;
        var client = new FakeInferenceProvider
        {
            LoadedContextWindow = 4_096,
            OnChatRequest = (_, messages, _, _) =>
            {
                requestChars = messages.Sum(m => (m.Content ?? string.Empty).Length);
                return Task.FromResult(new ChatTurnResult("SUMMARY", null, 1, 1));
            },
        };

        var decision = await ContextManager.PrepareAsync(Conversation(turns: 30, answerChars: 3_000), config, client,
                                                         lastPromptTokens: 100_000, onStep: null, CancellationToken.None);

        Assert.Equal(ContextOutcome.Compacted, decision.Outcome);
        Assert.Contains("utility-small", client.LoadedContextQueries);
        Assert.True(requestChars <= 4_096 * 4, $"{requestChars} characters sent to a model loaded with 4 096 tokens");
    }

    [Fact]
    public async Task ASummaryRequest_DoesNotCarryTheSystemPrompt()
    {
        // The summarizer needs the conversation, not the pinned files and rules the prompt carries.
        var (_, _, request) = await Compact(Conversation(turns: 10, answerChars: 500));

        Assert.DoesNotContain("SYSTEM PROMPT", request);
    }

    [Fact]
    public async Task ASummaryOfPartOfTheSlice_SaysSo_ToBothReaders()
    {
        var (decision, _, request) = await Compact(Conversation(turns: 30, answerChars: 3_000));

        Assert.Contains("question 27", request);                                   // the most recent part is kept
        Assert.DoesNotContain("question 0\n", request.Replace("\r\n", "\n"));    // the earliest did not fit
        Assert.True(decision.IsDegraded);
        // Both readers, with the same count — and not the length-limit sentence: that is another cause.
        var omitted = ReadOmitted(decision.Summary!);
        Assert.True(omitted > 0);
        Assert.Contains(Strings.MsgContextSummaryPartial(omitted, decision.Plan.Count), decision.Notice);
        Assert.DoesNotContain(Strings.MsgContextSummaryCut, decision.Notice);
    }

    private static int ReadOmitted(string summary)
    {
        var m = System.Text.RegularExpressions.Regex.Match(summary, @"its first (\d+) message\(s\) did not fit");
        Assert.True(m.Success, "the model-facing marker does not say how many messages are missing");
        return int.Parse(m.Groups[1].Value);
    }

    private sealed class RecordingChatClient : IOllamaChatClient
    {
        public int RequestChars { get; private set; }

        public Task<ChatTurnResult> SendChatAsync(
            string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal, string? toolChoice = null,
            Action<string>? onThinking = null)
        {
            RequestChars = messages.Sum(m => (m.Content ?? string.Empty).Length);
            return Task.FromResult(new ChatTurnResult("Read the files; the bug is in Parser.cs.", null, 0, 0));
        }
    }

    [Fact]
    public async Task ARunSummary_IsBoundedToo_AndSaysWhatItMisses()
    {
        // The agent run's own summary: its slice is tool output, the largest there is.
        var client = new RecordingChatClient();
        var orch   = new AgentOrchestrator(client, new InferpalConfig { ContextWindowSize = 8_192, CompactionEnabled = true });
        var msgs = new List<ChatMessageDto> { new("system", "s"), new("user", "go") }
            .Concat(Enumerable.Range(0, 20).Select(i => new ChatMessageDto("tool", $"result {i} " + new string('x', 8_000))))
            .ToList();

        await orch.CompactRunContextAsync(msgs, anchorCount: 2, model: "m", alreadySummarized: false,
                                          onStep: _ => { }, ct: CancellationToken.None);

        var summary = msgs[3];
        Assert.StartsWith("Read the files", summary.Content);                                          // witness
        Assert.True(client.RequestChars <= 8_192 * 4, $"the run summary request carried {client.RequestChars} characters");
        Assert.Contains("[Context Note] This summary covers only the most recent part", summary.Content);
    }

    [Fact]
    public async Task ASliceThatFits_IsSummarisedWhole_AndSaysNothingMore()
    {
        // Reference arm.
        var (decision, _, request) = await Compact(Conversation(turns: 10, answerChars: 500));

        Assert.Contains("question 0", request);
        Assert.Equal("SUMMARY", decision.Summary);
        Assert.False(decision.IsDegraded);
    }
}
