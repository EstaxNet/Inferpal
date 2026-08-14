using System.Text;

namespace Inferpal.Services.Bench;

/// <summary>Measured totals of one arm of a context-bench comparison.</summary>
/// <param name="Label">Human label of the arm ("map off" / "map on").</param>
/// <param name="Correct">Questions answered with the right file.</param>
/// <param name="Total">Questions asked (identical across arms by construction).</param>
/// <param name="ToolCalls">Tool calls executed across the whole arm.</param>
/// <param name="PromptTokens">Prompt tokens consumed across the whole arm — the cost being measured.</param>
internal sealed record ContextBenchArm(
    string Label, int Correct, int Total, int ToolCalls, int PromptTokens);

/// <summary>What the comparison concluded — see <see cref="ContextBenchReport.Judge"/>.</summary>
internal enum ContextBenchVerdict
{
    /// <summary>At least as correct, and cheaper in prompt tokens.</summary>
    Pays,
    /// <summary>More correct but more expensive: a judgement call, not a win.</summary>
    Tradeoff,
    /// <summary>As correct or less, and no cheaper.</summary>
    DoesNotPay,
    /// <summary>Fewer correct answers: no token saving can redeem it.</summary>
    Regression,
    /// <summary>Nothing was measured.</summary>
    Inconclusive,
}

/// <summary>
/// Compares two arms of the context bench and states, in one line, whether the extra context paid
/// for itself.
/// </summary>
/// <remarks>
/// <para>
/// The whole point of this class is to make the measurement <b>refuse to flatter the feature</b>.
/// Two dishonest readings are easy to produce and both are blocked here:
/// </para>
/// <list type="bullet">
///   <item>Counting only prompt tokens would show the repository map as a pure ~800-token cost,
///         hiding every tool call it avoided.</item>
///   <item>Counting only tool calls would reward a model that gave up sooner. Fewer calls with
///         fewer correct answers is a <see cref="ContextBenchVerdict.Regression"/>, whatever the
///         token column says.</item>
/// </list>
/// </remarks>
internal static class ContextBenchReport
{
    /// <summary>Token differences under this share of the baseline are noise, not a result.</summary>
    private const double NoiseSharePct = 2.0;

    /// <summary>Judges the comparison. Accuracy dominates: cost only breaks ties.</summary>
    public static ContextBenchVerdict Judge(ContextBenchArm without, ContextBenchArm with)
    {
        if (without.Total == 0 || with.Total == 0) return ContextBenchVerdict.Inconclusive;

        if (with.Correct < without.Correct) return ContextBenchVerdict.Regression;

        var delta   = with.PromptTokens - without.PromptTokens;
        var noise   = without.PromptTokens * NoiseSharePct / 100.0;
        var cheaper = delta < -noise;

        if (cheaper) return ContextBenchVerdict.Pays;
        if (with.Correct > without.Correct) return ContextBenchVerdict.Tradeoff;
        return ContextBenchVerdict.DoesNotPay;
    }

    /// <summary>Renders the markdown block shown in the chat.</summary>
    /// <param name="setting">
    /// Configuration key the verdict is about, named in the
    /// recommendation so a negative result is actionable rather than merely discouraging.
    /// </param>
    public static string Render(ContextBenchArm without, ContextBenchArm with, string setting)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### Context bench");
        sb.AppendLine();
        sb.AppendLine("| Arm | Correct | Tool calls | Prompt tokens |");
        sb.AppendLine("|---|---|---|---|");
        sb.AppendLine(Row(without));
        sb.AppendLine(Row(with));
        sb.AppendLine();

        var verdict = Judge(without, with);
        var delta   = with.PromptTokens - without.PromptTokens;
        var calls   = without.ToolCalls - with.ToolCalls;

        sb.AppendLine(verdict switch
        {
            ContextBenchVerdict.Pays =>
                $"**It pays.** {Signed(-delta)} prompt tokens and {calls} fewer tool calls, "
                + $"for {Accuracy(with)} correct instead of {Accuracy(without)}.",

            ContextBenchVerdict.Tradeoff =>
                $"**Trade-off.** More correct ({Accuracy(with)} vs {Accuracy(without)}) but "
                + $"{Signed(delta)} prompt tokens. Worth it only if accuracy is what you are short of.",

            ContextBenchVerdict.DoesNotPay =>
                $"**It does not pay on this repository.** No accuracy gained "
                + $"({Accuracy(with)} vs {Accuracy(without)}) and {Signed(delta)} prompt tokens. "
                + $"Consider setting `{setting}` to false.",

            ContextBenchVerdict.Regression =>
                $"**Worse.** {Accuracy(with)} correct against {Accuracy(without)} without it — "
                + $"fewer right answers is not redeemed by any token count. Set `{setting}` to false.",

            _ => "**Inconclusive** — no question could be generated for this repository.",
        });

        return sb.ToString().TrimEnd();
    }

    private static string Row(ContextBenchArm a) =>
        $"| {a.Label} | {a.Correct}/{a.Total} | {a.ToolCalls} | {a.PromptTokens:N0} |";

    private static string Accuracy(ContextBenchArm a) => $"{a.Correct}/{a.Total}";

    private static string Signed(int n) => n >= 0 ? $"+{n:N0}" : $"−{-n:N0}";
}
