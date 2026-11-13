using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Xunit;
using Xunit.Abstractions;

namespace Inferpal.Tests;

/// <summary>
/// The answer always has room: a run's compaction brings the request under its target even when the recent tail does not
/// fit — the tail's results elided oldest first, the last batch cut (beginning and end kept, the cut said), never elided.
/// </summary>
/// <remarks>
/// Sizes of the <c>read20</c> probe (pari 68): a head of 5 027 characters of system prompt and 1 308 of question, tool
/// definitions of 17 812, reads of up to 8 058 characters, each round an assistant call and an observe prompt. Before this,
/// the five last messages stayed whole whatever their size: at 8K every request from the third went past the window, at
/// 16K up to 13 270 estimated tokens of 16 384 — the answer had a few hundred.
/// </remarks>
public sealed class AnswerRoomCompactionTests(ITestOutputHelper output)
{
    private const int ToolTokens = 17812 / 4;
    // How much the server counts over the estimate on code (AgentOrchestrator.ServerScale): the answers cut short at 16K came with it.
    private const double Scale = 1.3;
    private static readonly int[] ReadSizes = [8035, 7951, 5503, 6198, 7804, 3012, 8000, 557, 7120, 4480];

    private static List<ChatMessageDto> Head() =>
        [new("system", new string('s', 5027)), new("user", new string('q', 1308))];

    /// <summary>An assistant message that called <paramref name="calls"/> tools.</summary>
    private static ChatMessageDto Calls(int calls = 1) =>
        new("assistant", new string('a', 100),
            [.. Enumerable.Range(0, calls).Select(_ => new ToolCallDto(new ToolCallFunction("read_file", JsonDocument.Parse("{}").RootElement)))]);

    private static void Round(List<ChatMessageDto> run, int size)
    {
        run.Add(Calls());
        run.Add(new("tool", new string('r', size)));
        run.Add(new("user", new string('o', 390)));   // the observe prompt
    }

    private static int Weighed(List<ChatMessageDto> run) => AgentOrchestrator.EstimateTokens(run) + ToolTokens;

    [Fact]
    public void At16K_EveryRequestOfTwentyReads_IsUnderTheTrigger()
    {
        var run = Head();
        var trigger = AgentOrchestrator.CompactionBudget(16384).Trigger;   // what is under it is not compacted: 20 % are the answer's
        for (var read = 0; read < 20; read++)
        {
            Round(run, ReadSizes[read % ReadSizes.Length]);
            AgentOrchestrator.CompactRunContext(run, anchorCount: 2, budget: 16384, toolTokens: ToolTokens, scale: Scale);
            Assert.True(Weighed(run) * Scale <= trigger, $"read {read + 1}: {Weighed(run) * Scale} > {trigger}");
        }
        // The last result is never emptied.
        Assert.True(run[^2].Content!.Length >= AgentOrchestrator.MinCutResultChars);
    }

    /// <summary>At 8K the head and each round's call and observe prompt never elide (pari 77's limit): reported, not
    /// required — how many reads leave the request under the window.</summary>
    [Fact]
    public void At8K_HowManyReadsFit_IsReported()
    {
        var run = Head();
        var fit = 0;
        for (var read = 0; read < 20; read++)
        {
            Round(run, ReadSizes[read % ReadSizes.Length]);
            AgentOrchestrator.CompactRunContext(run, anchorCount: 2, budget: 8192, toolTokens: ToolTokens);
            if (Weighed(run) <= 8192) fit++;
        }
        output.WriteLine($"8K: {fit} of 20 requests under the window");
        Assert.True(fit >= 1);
    }

    [Fact]
    public void AParallelBatch_IsCut_EveryResultKept()
    {
        var run = Head();
        Round(run, 6000);
        run.Add(Calls(3));
        run.AddRange(Enumerable.Range(0, 3).Select(_ => new ChatMessageDto("tool", new string('r', 8000))));

        AgentOrchestrator.CompactRunContext(run, anchorCount: 2, budget: 8192, toolTokens: 1000);

        var batch = run.Skip(run.Count - 3).ToList();
        Assert.All(batch, m => Assert.True(m.Content!.Length >= AgentOrchestrator.MinCutResultChars && m.Content.Length < 8000));
        Assert.All(batch, m => Assert.Contains("truncated", m.Content));
        Assert.Contains("elided", run[3].Content);   // the earlier read went first
    }

    [Fact]
    public void ACutCommand_KeepsItsVerdict_AtItsEnd()
    {
        var run = Head();
        run.Add(Calls());
        run.Add(new("tool", new string('x', 7900) + "\n[exit code 1]"));

        AgentOrchestrator.CompactRunContext(run, anchorCount: 2, budget: 8192, toolTokens: ToolTokens);

        Assert.True(run[^1].Content!.Length < 7900);
        Assert.EndsWith("[exit code 1]", run[^1].Content);
    }

    /// <summary>A nudge leaves a text-only assistant message after the results the model must answer from: they are the
    /// last batch still, cut and never elided.</summary>
    [Fact]
    public void ANudgeAfterTheLastResults_LeavesThemTheLastBatch()
    {
        var run = Head();
        Round(run, 8000);
        Round(run, 8000);
        run.Add(new("assistant", "{\"goal\":\"…\"}"));   // a plan echoed
        run.Add(new("user", "That is the plan again — answer now."));

        AgentOrchestrator.CompactRunContext(run, anchorCount: 2, budget: 8192, toolTokens: ToolTokens);

        Assert.DoesNotContain("elided", run[6].Content);   // the second round's read: the last batch
        Assert.Contains("elided", run[3].Content);
    }

    /// <summary>A result cut on its way in, cut again: the mark counts from what it was.</summary>
    [Fact]
    public void AResultCutTwice_SaysHowLongItWas()
    {
        var once  = AgentOrchestrator.CapForContext(new string('x', 50_000));
        var twice = AgentOrchestrator.CapForContext(once, 2000);
        Assert.Contains("of 50000 characters", twice);
    }

    /// <summary>What a short result of the batch does not need goes to the longer ones.</summary>
    [Fact]
    public void AShortResultOfTheBatch_LeavesItsShareToTheOthers()
    {
        var run = Head();
        run.Add(Calls(3));
        run.Add(new("tool", new string('r', 500)));
        run.Add(new("tool", new string('r', 8000)));
        run.Add(new("tool", new string('r', 8000)));

        AgentOrchestrator.CompactRunContext(run, anchorCount: 2, budget: 8192, toolTokens: 1000);

        Assert.Equal(500, run[3].Content!.Length);
        Assert.True(AgentOrchestrator.EstimateTokens(run) + 1000 <= AgentOrchestrator.CompactionBudget(8192).Target);
    }

    [Fact]
    public void TheSummaryAndTheElision_ReadOneBudget() =>
        Assert.Equal((8000, 7000), AgentOrchestrator.CompactionBudget(10000));
}
