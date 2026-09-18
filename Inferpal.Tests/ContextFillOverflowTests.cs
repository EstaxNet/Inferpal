using System.Linq;
using Inferpal.Services.Commands;
using Inferpal.Services.Presentation;
using Inferpal.Services.Prompting;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A prompt that no longer fits the context window read as <b>exactly full</b>, in all three places
/// that show the figure.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <c>Math.Min(100.0, …)</c> belongs to a <b>bar</b>, which cannot be wider than itself; it had
/// been applied to the <b>value</b>, and the value is what the user reads: the header gauge's
/// tooltip ("N of M tokens, 100 %"), the <c>/xray</c> budget line and the interactive panel's label
/// all said 100 % for a conversation sitting at 187 % of the window.
/// </para>
/// <para>
/// ⚠ <b>The difference is the one that matters.</b> At exactly full, one more turn is a question; at
/// 187 % the backend is already dropping the head of the conversation — system prompt included —
/// without saying so, which is a failure mode this repository documents elsewhere. The bars were
/// never at risk: the markdown one clamps its own fill, WPF's <c>ProgressBar</c> coerces into
/// <c>[Minimum, Maximum]</c>, and the VS Code panel renders the figure as text only.
/// </para>
/// <para>
/// Same shape as the bench throughput of the previous round, one layer up: a derived number quietly
/// trimmed reads as a number, never as a fragment.
/// </para>
/// </remarks>
public sealed class ContextFillOverflowTests
{
    // ── The header gauge ──────────────────────────────────────────────────────

    [Fact]
    public void TheGauge_SaysHowFarPastTheWindowItIs()
    {
        var budget = ContextBudgetGauge.Compute(promptTokens: 15_000, limit: 8_192);

        Assert.NotNull(budget);
        Assert.True(budget!.FillPercent > 100, $"still clamped at {budget.FillPercent}.");
        Assert.Equal(183.0, budget.FillPercent, 0);
        Assert.Contains("183", budget.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGauge_UnderTheWindow_IsUnchanged()
    {
        // REFERENCE ARM: only the overflow changes. Without it, a fix that scaled or shifted the
        // percentage would pass the test above.
        var budget = ContextBudgetGauge.Compute(promptTokens: 2_048, limit: 8_192);

        Assert.NotNull(budget);
        Assert.Equal(25.0, budget!.FillPercent, 3);
        Assert.Equal("#606060", budget.Color);
    }

    [Fact]
    public void TheGauge_PastTheWindow_IsStillRed()
    {
        // The colour thresholds are open-ended upwards; an unclamped value must not fall out of them.
        Assert.Equal("#CC2222", ContextBudgetGauge.Compute(15_000, 8_192)!.Color);
    }

    // ── /xray, markdown ───────────────────────────────────────────────────────

    private static PromptSection Section(string content) =>
        new(PromptSectionKind.Base, Detail: null, Content: content);

    [Fact]
    public void TheXrayBudgetLine_SaysHowFarPastTheWindowItIs()
    {
        // 2 000 characters ≈ 500 tokens of prompt, plus 4 000 tokens of history, against a window
        // of 1 000: 450 %.
        var md = XRayCommandHandler.Handle(
            [Section(new string('x', 2_000))], historyTokens: 4_000, contextWindow: 1_000, ragAutoContext: false);

        Assert.Contains("450", md, StringComparison.Ordinal);
        Assert.DoesNotContain("100 %", md, StringComparison.Ordinal);
    }

    [Fact]
    public void TheXraySectionBars_NeverOverflowTheirWidth()
    {
        // REFERENCE ARM: the per-section bars are shares of the total and must stay inside their
        // width — that is where a clamp genuinely belongs, and it is not the one that was removed.
        var md = XRayCommandHandler.Handle(
            [Section(new string('x', 2_000)), Section(new string('y', 40))],
            historyTokens: 4_000, contextWindow: 1_000, ragAutoContext: false);

        foreach (var line in md.Split('\n').Where(l => l.Contains('█') || l.Contains('░')))
            Assert.Equal(10, line.Count(c => c is '█' or '░'));
    }

    // ── The interactive panel ─────────────────────────────────────────────────

    [Fact]
    public void ThePanel_SaysHowFarPastTheWindowItIs()
    {
        var model = XRayPanelPresenter.Build(
            [Section(new string('x', 2_000))], disabledIds: null, historyTokens: 4_000, contextWindow: 1_000);

        Assert.True(model.FillPercent > 100, $"still clamped at {model.FillPercent}.");
        Assert.Equal(450.0, model.FillPercent, 0);
    }

    [Fact]
    public void ThePanel_UnderTheWindow_IsUnchanged()
    {
        var model = XRayPanelPresenter.Build(
            [Section(new string('x', 2_000))], disabledIds: null, historyTokens: 0, contextWindow: 1_000);

        Assert.Equal(50.0, model.FillPercent, 0);
    }
}
