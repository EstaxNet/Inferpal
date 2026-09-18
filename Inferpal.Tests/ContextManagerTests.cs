using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Host;
using Inferpal.Services.Agent;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The context pre-check, and the fact that it exists on <b>both</b> sides.
/// </summary>
/// <remarks>
/// ⚠ It lived entirely in the Visual Studio window. On the VS Code side the history was therefore
/// NEVER bounded: it grew until it passed the model's <c>num_ctx</c>, and it was the backend that
/// cut the head off the conversation — system prompt included — without anything saying so. Seen
/// from the user: an assistant that forgets.
///
/// And the VS Code settings panel offered <c>compactionEnabled</c>,
/// <c>contextWindowKeepTurns</c> and <c>compactionTimeoutSeconds</c>: three controls rendered as
/// functional, with no effect whatsoever.
/// </remarks>
public class ContextManagerTests
{
    private static InferpalConfig Config(bool compaction) => new()
    {
        ContextWindowSize      = 1000,
        ContextWindowKeepTurns = 2,
        CompactionEnabled      = compaction,
        KvCacheAnchorMessages  = 0,
        CompactionTimeoutSeconds = 10,
    };

    private static List<ChatMessageDto> LongHistory()
    {
        var h = new List<ChatMessageDto> { new("system", "sys") };
        for (var i = 0; i < 20; i++)
        {
            h.Add(new ChatMessageDto("user", $"question {i}"));
            h.Add(new ChatMessageDto("assistant", $"answer {i}"));
        }
        return h;
    }

    /// <summary>Reference arm: under budget, nothing happens and nothing is said.
    /// <c>lastPromptTokens == 0</c> is a first turn's state.</summary>
    [Fact]
    public async Task UnderBudget_NothingHappensAndNothingIsSaid()
    {
        var decision = await ContextManager.PrepareAsync(
            LongHistory(), Config(compaction: true), new FakeInferenceProvider(),
            lastPromptTokens: 0, onStep: null, ct: CancellationToken.None);

        Assert.Equal(ContextOutcome.None, decision.Outcome);
        Assert.Equal(string.Empty, decision.Notice);
    }

    /// <summary>Compaction disabled: we truncate, and we SAY so — the conversation has just lost
    /// turns.</summary>
    [Fact]
    public async Task OverBudgetWithoutCompaction_TruncatesAndSaysSo()
    {
        var decision = await ContextManager.PrepareAsync(
            LongHistory(), Config(compaction: false), new FakeInferenceProvider(),
            lastPromptTokens: 100_000, onStep: null, ct: CancellationToken.None);

        Assert.Equal(ContextOutcome.Truncated, decision.Outcome);
        Assert.NotEqual(string.Empty, decision.Notice);
        Assert.Null(decision.Summary);
    }

    /// <summary>Compaction succeeded: the summary comes back, ready for the caller to apply.</summary>
    [Fact]
    public async Task OverBudgetWithCompaction_ReturnsTheSummary()
    {
        var client = new FakeInferenceProvider { ChatResult = new("here is the summary", null, 0, 0) };

        var decision = await ContextManager.PrepareAsync(
            LongHistory(), Config(compaction: true), client,
            lastPromptTokens: 100_000, onStep: null, ct: CancellationToken.None);

        Assert.Equal(ContextOutcome.Compacted, decision.Outcome);
        Assert.Equal("here is the summary", decision.Summary);
        Assert.NotEqual(string.Empty, decision.Notice);
    }

    /// <summary>
    /// ⚠ The fuse: an empty summary is not a compaction. We truncate, and the message is NOT the
    /// success one — a degraded result and the requested result look alike on the history and not
    /// at all to the user.
    /// </summary>
    [Fact]
    public async Task WhenTheSummaryDoesNotCome_ItFallsBackAndSaysSomethingElse()
    {
        var ok = await ContextManager.PrepareAsync(
            LongHistory(), Config(compaction: true), new FakeInferenceProvider { ChatResult = new("summary", null, 0, 0) },
            lastPromptTokens: 100_000, onStep: null, ct: CancellationToken.None);

        var fell = await ContextManager.PrepareAsync(
            LongHistory(), Config(compaction: true), new FakeInferenceProvider { ChatResult = new("   ", null, 0, 0) },
            lastPromptTokens: 100_000, onStep: null, ct: CancellationToken.None);

        Assert.Equal(ContextOutcome.CompactionFellBack, fell.Outcome);
        Assert.Null(fell.Summary);
        Assert.NotEqual(ok.Notice, fell.Notice);
    }

    // ── Parity, which is the real subject ─────────────────────────────────────

    /// <summary>
    /// ⚠ A summary call that FAILS is not a summary. The old route went through
    /// <c>RunAgentAsync</c>, which returns the network error as <c>FinalResponse</c>: the message
    /// "cannot reach the backend" became the summary, and the old turns were replaced by it, under
    /// a success notice.
    /// </summary>
    [Fact]
    public async Task ASummaryCallThatFails_IsNotTakenForTheSummary()
    {
        const string unreachable = "Cannot reach the backend at http://localhost:11434";
        var client = new FakeInferenceProvider
        {
            ChatResult = new(unreachable, null, 0, 0),
            OnChat     = (_, _) => throw new AgentHttpException(unreachable, isTimeout: false),
        };

        var decision = await ContextManager.PrepareAsync(
            LongHistory(), Config(compaction: true), client,
            lastPromptTokens: 100_000, onStep: null, ct: CancellationToken.None);

        Assert.Equal(ContextOutcome.CompactionFellBack, decision.Outcome);
        Assert.Null(decision.Summary);
    }

    /// <summary>
    /// BOTH front-ends do the pre-check. Without this rule the original version of the defect comes
    /// back with nothing going red: the service would exist, and a single side would call it.
    /// </summary>
    [Theory]
    [InlineData("Inferpal", "ToolWindow", "InferpalToolWindowData.Rag.cs")]
    [InlineData("Inferpal.Host", "HostServer.cs")]
    public void BothFrontEndsRunThePreSendCheck(params string[] parts)
    {
        var file = Path.Combine(RepoRoot(), Path.Combine(parts));
        var code = ConventionCoverageTests.CodeOnly(file);

        Assert.Contains("ContextManager.PrepareAsync", code, StringComparison.Ordinal);
        Assert.Contains("HistoryCompaction.ApplySummary", code, StringComparison.Ordinal);
        Assert.Contains("HistoryCompaction.ApplyTruncation", code, StringComparison.Ordinal);
    }

    /// <summary>The host keeps the previous turn's prompt tokens — without them
    /// <see cref="HistoryCompaction.Decide"/> always answers "nothing to do", and the pre-check
    /// would be present but inert. Exactly the kind of repair that believes itself done.</summary>
    [Fact]
    public void TheHostRemembersTheLastPromptTokens()
    {
        var host = ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(), "Inferpal.Host", "HostServer.cs"));

        // Agent path: estimated on the durable history kept after the run (question + answer), not on
        // the run's discarded internal transcript — the VS window does the same.
        Assert.Contains("s.LastPromptTokens = Services.Agent.AgentOrchestrator.EstimateTokens(s.History)", host, StringComparison.Ordinal);
        Assert.Contains("s.LastPromptTokens = turn.PromptTokens",   host, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // ── An empty turn: the same fallback chain on both sides ──────────────────

    /// <summary>
    /// ⚠ The chain "text → tool summary → a message naming what was observed" lived only in the
    /// Visual Studio window: on the VS Code side a turn with no text ended <b>in silence</b>. That
    /// is the defect 1.6.8 puts at the top of its notes, repaired on one side only — the diagnostic
    /// half (in the Core) was repaired on both.
    /// </summary>
    [Fact]
    public void TheEmptyTurnFallbackChain_ExistsInBothFrontEnds()
    {
        var vm   = ConventionCoverageTests.CodeOnly(Path.Combine(
            RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.ChatTurn.cs"));
        var host = ConventionCoverageTests.CodeOnly(Path.Combine(
            RepoRoot(), "Inferpal.Host", "HostServer.cs"));

        foreach (var code in new[] { vm, host })
        {
            // The SAME decision, not a second implementation.
            Assert.Contains("ChatTurnPolicy.DecideFinalAnswer", code, StringComparison.Ordinal);
            // The two fallbacks that make the difference between "nothing" and "here is what".
            Assert.Contains("MsgAgentDone",          code, StringComparison.Ordinal);
            Assert.Contains("MsgEmptyResponseFrom",  code, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// ⚠ A defect I INTRODUCED while wiring the pre-check on the host side, found by re-reading:
    /// the host replaces <c>History</c> in three places — <c>/clear</c>, session load, branch
    /// creation — and did not reset the token counter. The next turn would have decided on the
    /// prompt size of a conversation just left: a compaction triggered on a short conversation,
    /// hence a model call for nothing and turns thrown away.
    ///
    /// The rule is carried by the <b>setter</b> rather than by three calls: one can no longer
    /// replace the history and forget the counter.
    /// </summary>
    [Fact]
    public void ReplacingTheHistory_ResetsThePromptTokenCount()
    {
        var session = typeof(HostSession);
        var history = session.GetProperty("History");
        var tokens  = session.GetProperty("LastPromptTokens");

        Assert.NotNull(history);
        Assert.NotNull(tokens);

        // Witness: the setter exists and is not auto-implemented (an auto-property cannot carry the
        // rule). The backing field named _history is the direct proof.
        var host = ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(), "Inferpal.Host", "HostSession.cs"));
        Assert.Contains("set { _history = value; LastPromptTokens = 0; }", host, StringComparison.Ordinal);
    }
}
