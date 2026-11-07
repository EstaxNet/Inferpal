using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A compaction summary that reasons for minutes sends chunks no editor sees: the prefill wait has given its place back,
/// and the step line said "Compacting context…" once, for the whole summary. The fuse that sees every chunk says the
/// step again, with the time and the tokens so far — at the first chunk, then every beat.
/// </summary>
[Collection(CultureSerialCollection.Name)]
public class SummaryProgressTests
{
    /// <summary>The line said with any elapsed time: what comes before it and after it.</summary>
    private static (string Head, string Tail) Around(int tokens)
    {
        var parts = Strings.StatusCompactingProgress("\u0001", tokens).Split('\u0001');
        return (parts[0], parts[1]);
    }

    internal static bool IsProgress(string step, int tokens)
    {
        var (head, tail) = Around(tokens);
        return step.StartsWith(head, StringComparison.Ordinal) && step.EndsWith(tail, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStep_IsSaidAtTheFirstChunk_ThenEveryBeat()
    {
        var now = TimeSpan.Zero;
        var said = new List<string>();
        using var cts = new CancellationTokenSource();
        var fuse = new SummaryFuse(cts, 60, said.Add, () => now);

        // A chunk of 40 characters every second, for 12 seconds.
        for (var s = 0; s <= 12; s++)
        {
            now = TimeSpan.FromSeconds(s);
            fuse.Progress(new string('r', 40));
        }

        Assert.Equal(new[]
        {
            Strings.StatusCompactingProgress(RunSummary.Duration(TimeSpan.Zero), 10),
            Strings.StatusCompactingProgress(RunSummary.Duration(TimeSpan.FromSeconds(5)), 60),
            Strings.StatusCompactingProgress(RunSummary.Duration(TimeSpan.FromSeconds(10)), 110),
        }, said);
    }

    [Fact]
    public void AFirstChunkOfTwoCharacters_IsOneToken_NeverZero()
    {
        var said = new List<string>();
        using var cts = new CancellationTokenSource();
        new SummaryFuse(cts, 60, said.Add, () => TimeSpan.Zero).Progress("We");

        Assert.Equal(Strings.StatusCompactingProgress(RunSummary.Duration(TimeSpan.Zero), 1), Assert.Single(said));
    }

    [Theory]
    [InlineData("en")] [InlineData("fr")] [InlineData("de")] [InlineData("es")] [InlineData("it")]
    [InlineData("ru")] [InlineData("ja")] [InlineData("ko")] [InlineData("pl")] [InlineData("zh-CN")]
    public void ATasksJournal_KeepsOneStepForASummary_AndOneForAWait(string culture)
    {
        Strings.ApplyLanguage(culture);
        try
        {
            // The progress begins with the step it continues, in every language: that is how a journal knows it.
            var progress = Strings.StatusCompactingProgress(RunSummary.Duration(TimeSpan.FromSeconds(5)), 60);
            Assert.True(StepLines.SaysAgain(Strings.StatusCompacting, progress), progress);
            Assert.True(StepLines.SaysAgain(progress, Strings.StatusCompactingProgress("1 min 05 s", 900)));

            var wait = new ModelWait(4_000, TimeSpan.FromSeconds(9), null, TimeSpan.FromMinutes(2));
            var later = wait with { Elapsed = TimeSpan.FromSeconds(14) };
            Assert.True(StepLines.SaysAgain(wait.Short, later.Short));

            // Anything else is a step of its own, a repeated one included.
            Assert.False(StepLines.SaysAgain(Strings.StatusCompacting, Strings.StatusAgentSynthesizing));
            Assert.False(StepLines.SaysAgain(wait.Short, Strings.StatusCompacting));
            Assert.False(StepLines.SaysAgain(Strings.StatusAgentSynthesizing, Strings.StatusAgentSynthesizing));
        }
        finally { Strings.ApplyLanguage(null); }
    }

    /// <summary>A task's run has no wait scope (it outlives its gesture), so a slow read says its wait in the task's steps,
    /// every beat (<c>ModelWaitScope.OrStep</c>): one step for the wait, the last line kept.</summary>
    [Fact]
    public async Task ATaskThatWaitsOnASlowRead_CountsOneStep_AndKeepsWhereItGotTo()
    {
        var wait = new ModelWait(30_000, TimeSpan.FromSeconds(8), null, TimeSpan.FromMinutes(5));
        var last = (wait with { Elapsed = TimeSpan.FromSeconds(18) }).Short;
        using var queue = new Inferpal.Services.Tasks.BackgroundTaskQueue((_, onStep, _) =>
        {
            onStep(wait.Short);
            onStep((wait with { Elapsed = TimeSpan.FromSeconds(13) }).Short);
            onStep(last);
            onStep(Strings.StatusThinking);
            return Task.FromResult(Inferpal.Services.Tasks.BackgroundTaskQueue.TaskRunOutcome.Of("done"));
        });
        var id = queue.Submit("audit")!;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (queue.Get(id)?.IsFinished != true)
        {
            Assert.True(DateTime.UtcNow < deadline, "the task did not finish");
            await Task.Delay(10);
        }

        var task = queue.Get(id)!;
        Assert.Equal(2, task.StepsTaken);
        Assert.Equal(new[] { last, Strings.StatusThinking }, task.Steps);
    }

    [Fact]
    public void AChunkAfterTheCallReturned_SaysNothing()
    {
        var said = new List<string>();
        var cts = new CancellationTokenSource();
        var fuse = new SummaryFuse(cts, 60, said.Add, () => TimeSpan.Zero);
        cts.Dispose();

        fuse.Progress("late");

        Assert.Empty(said);
    }

    [Fact]
    public async Task TheConversationsSummary_SaysHowFarItHasCome()
    {
        var config = new InferpalConfig
        {
            ContextWindowSize = 8_192, ContextWindowKeepTurns = 2, CompactionEnabled = true, KvCacheAnchorMessages = 0,
        };
        var client = new FakeInferenceProvider
        {
            DriveThinking = think => think(new string('r', 400)),
            OnChat = (_, _) => Task.FromResult(new ChatTurnResult("summary of the early turns", null, 0, 0)),
        };
        var history = new List<ChatMessageDto> { new("system", "sys") };
        for (var i = 0; i < 8; i++)
        {
            history.Add(new ChatMessageDto("user", $"question {i} " + new string('q', 1_000)));
            history.Add(new ChatMessageDto("assistant", $"answer {i} " + new string('a', 1_000)));
        }
        var steps = new List<string>();

        var decision = await ContextManager.PrepareAsync(history, config, client, lastPromptTokens: 100_000,
                                                         onStep: steps.Add, CancellationToken.None);

        Assert.Equal(ContextOutcome.Compacted, decision.Outcome);   // witness: the summary was asked for and kept
        Assert.Contains(Strings.StatusCompacting, steps);
        Assert.Contains(steps, s => IsProgress(s, 100));
    }

    /// <summary>Answers the run's summary request after streaming its reasoning.</summary>
    private sealed class ReasoningSummarizer : IChatTurnClient
    {
        public int Calls { get; private set; }

        public Task<ChatTurnResult> SendChatAsync(
            string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal, string? toolChoice = null,
            Action<string>? onThinking = null)
        {
            Calls++;
            onThinking?.Invoke(new string('r', 400));
            return Task.FromResult(new ChatTurnResult("SUMMARY OF OLD TURNS", null, 0, 0));
        }
    }

    [Fact]
    public async Task TheRunsSummary_SaysHowFarItHasCome()
    {
        var client = new ReasoningSummarizer();
        var orch = new AgentOrchestrator(client, new InferpalConfig { ContextWindowSize = 8192, CompactionEnabled = true });
        var messages = new List<ChatMessageDto> { new("system", new string('s', 4000)), new("user", "go") }
            .Concat(Enumerable.Range(0, 10).Select(_ => new ChatMessageDto("tool", new string('x', 8000))))
            .ToList();
        var steps = new List<string>();

        var spent = await orch.CompactRunContextAsync(messages, anchorCount: 2, model: "m",
            alreadySummarized: false, onStep: steps.Add, ct: CancellationToken.None);

        Assert.True(spent);
        Assert.Equal(1, client.Calls);   // witness: the summary was asked for
        Assert.Contains(Strings.StatusCompacting, steps);
        Assert.Contains(steps, s => IsProgress(s, 100));
    }
}
