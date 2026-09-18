using System.Collections.Generic;
using System.IO;
using System.Linq;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Compaction is the ONLY place the product throws conversation away — and when it throws it away
/// without a summary, neither the model nor (in VS Code) the user could tell.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>The asymmetry is in one file.</b> <see cref="HistoryCompaction.ApplySummary"/> leaves a
/// <c>[Context Summary]</c> marker followed by the summary: the model KNOWS something was condensed.
/// <see cref="HistoryCompaction.ApplyTruncation"/> did a bare <c>RemoveRange</c>: the conversation
/// has a hole and nothing says so. The model then reads an exchange that begins abruptly, which
/// reads as "this is all of it" — the user writes "as I told you, we use tabs" and is told it was
/// never mentioned. This is the doctrine the repository applies to every one of its tools (<i>a cap
/// that shapes a CONCLUSION declares itself</i>) and did not apply to the conversation itself.
/// </para>
/// <para>
/// ⚠ <b>And the marker has to be invisible to the TURN COUNT</b>: a <c>user</c> turn is the
/// product's unit of counting (<c>contextWindowKeepTurns</c>, <c>/branch</c> numbering, stepping
/// back on regeneration). <c>IsScaffolding</c> is exactly what keeps it out (<c>Decide</c> filters
/// on it), and that is what the reference arm checks: without that flag the fix would shift the turn
/// count of the whole product.
/// </para>
/// <para>
/// ⚠ <b>Second half: one datum, two readers.</b> The VS window rendered a successful compaction as a
/// collapsible tool bubble and the two fallbacks as warnings <i>in plain text</i> — the rule is
/// written in its own comment. The host sent all three as the same collapsed <c>context_compact</c>
/// bubble: the DEGRADED outcome was the one that looked routine. The rule now lives in
/// <c>ContextDecision.IsDegraded</c>, which both of them read.
/// </para>
/// </remarks>
public class TruncatedHistorySilenceTests
{
    /// <summary>"system, (user, assistant) × turns" — the shape of the durable history.</summary>
    private static List<ChatMessageDto> History(int turns)
    {
        var h = new List<ChatMessageDto> { new("system", "sys") };
        for (var i = 1; i <= turns; i++)
        {
            h.Add(new("user", $"question {i}"));
            h.Add(new("assistant", $"answer {i}"));
        }
        return h;
    }

    private static CompactionPlan Decide(List<ChatMessageDto> history, int keepTurns = 2, int kvAnchor = 0) =>
        HistoryCompaction.Decide(history, 1000, 900, keepTurns, kvAnchor, compactionEnabled: true);

    // ── What the MODEL reads ─────────────────────────────────────────────────

    [Fact]
    public void ATruncatedHistory_TellsTheModelThatSomethingWasDropped()
    {
        var history = History(5);
        var plan    = Decide(history);

        // WITNESS: the fixture really does throw something away. Without it, a plan that removes
        // nothing would keep this test green on a product that has not changed.
        Assert.Equal(CompactionAction.Compact, plan.Action);
        Assert.True(plan.Count > 0, "the plan removes no message: this test measured nothing.");

        HistoryCompaction.ApplyTruncation(history, plan);

        var marker = history[plan.Start];
        Assert.Equal(HistoryCompaction.TruncationMarker(plan.Count), marker.Content);
        // The real count, not a vague word: the model must be able to say WHAT it no longer has.
        Assert.Contains(plan.Count.ToString(), marker.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMarkerIsScaffolding_SoItIsNotCountedAsATurn()
    {
        // ⚠ The reference arm that matters: a non-scaffolding `user` would shift keepTurns,
        // /branch numbering and the step back on regeneration.
        var history = History(5);
        var plan    = Decide(history);
        HistoryCompaction.ApplyTruncation(history, plan);

        var marker = history[plan.Start];
        Assert.Equal("user", marker.Role);
        Assert.True(marker.IsScaffolding, "un marqueur non-scaffolding devient un tour utilisateur.");

        // And the proof by use: the turn count is that of the kept turns, not one more.
        var turns = history.Skip(1).Count(m => m.Role == "user" && !m.IsScaffolding);
        Assert.Equal(2, turns);
    }

    [Fact]
    public void TheKeptTail_IsUntouched()
    {
        // Reference arm: the marker is added, it neither shifts nor eats the conversation kept.
        var history = History(5);
        var plan    = Decide(history);
        HistoryCompaction.ApplyTruncation(history, plan);

        Assert.Equal("system", history[0].Role);
        Assert.Equal("question 4", history[plan.Start + 1].Content);
        Assert.Equal("answer 5", history[^1].Content);
    }

    [Fact]
    public void WithKvAnchors_TheMarkerLandsAfterThem_LikeTheSummaryPair()
    {
        // The KV-cache anchors are kept verbatim at the head: the marker lands AFTER them, exactly
        // where ApplySummary puts its own, otherwise the cached prefix changes.
        var history = History(5);
        var plan    = Decide(history, kvAnchor: 2);
        Assert.True(plan.KvAnchor > 0, "no anchor: this test measured nothing.");

        HistoryCompaction.ApplyTruncation(history, plan);

        Assert.Equal("question 1", history[1].Content);   // ancre verbatim
        Assert.Equal("answer 1",   history[2].Content);   // ancre verbatim
        Assert.Equal(HistoryCompaction.TruncationMarker(plan.Count), history[plan.Start].Content);
    }

    [Fact]
    public void ASummarisedHistory_StillCarriesItsOwnMarker_NotThisOne()
    {
        // Reference arm: the half that already worked does not change.
        var history = History(5);
        var plan    = Decide(history);
        HistoryCompaction.ApplySummary(history, plan, "the summary");

        Assert.Equal("[Context Summary]", history[plan.Start].Content);
        Assert.Equal("the summary", history[plan.Start + 1].Content);
    }

    // ── What the USER reads: one datum, two readers ──────────────────────────

    // ⚠ The outcome travels as an `int`: `ContextOutcome` is internal, and a public test method
    // cannot take one as a parameter (CS0051). The cast is in the attribute, not in the rule.
    [Theory]
    [InlineData((int)ContextOutcome.None,               false)]
    [InlineData((int)ContextOutcome.Compacted,          false)]
    [InlineData((int)ContextOutcome.Truncated,          true)]
    [InlineData((int)ContextOutcome.CompactionFellBack, true)]
    public void OnlyTheTwoFallbacks_AreDegraded(int outcome, bool degraded)
    {
        var decision = new ContextDecision((ContextOutcome)outcome, CompactionPlan.None, null, "notice");
        Assert.Equal(degraded, decision.IsDegraded);
    }

    [Theory]
    [InlineData("Inferpal", "ToolWindow", "InferpalToolWindowData.Rag.cs")]
    [InlineData("Inferpal.Host", "HostServer.cs")]
    public void BothFrontEnds_RenderTheDegradedOutcomeFromTheSHAREDRule(params string[] parts)
    {
        // The rule was written in the comment of one of its two readers, and the other did not hold
        // it. It now lives in the Core: both READ it, neither rewrites it.
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(), Path.Combine(parts)));

        // WITNESS: this really is a consumer of the context decision.
        Assert.Contains("ContextManager.PrepareAsync", code, StringComparison.Ordinal);
        Assert.Contains("IsDegraded", code, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
