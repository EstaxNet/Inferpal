using System.Globalization;

namespace Inferpal.Services.Presentation;

/// <summary>The context-window fill indicator's computed state.</summary>
internal sealed record ContextBudget(double FillPercent, string Color);

/// <summary>
/// Context-window fill gauge: maps the measured prompt size against the configured limit to a
/// fill percentage and a severity colour (green → amber → orange → red at the 50/80/95% thresholds).
/// Extracted from the tool-window VM so the threshold logic is unit-testable.
/// </summary>
internal static class ContextBudgetGauge
{
    /// <summary>
    /// Returns the gauge state, or <c>null</c> when there is nothing to show — no limit configured
    /// or no prompt measured yet — in which case the VM hides the indicator.
    /// </summary>
    public static ContextBudget? Compute(int promptTokens, int limit)
    {
        if (limit <= 0 || promptTokens <= 0) return null;

        // ⚠ NOT clamped to 100: the clamp belongs to a bar, which cannot be wider than itself, and
        // it had been applied to the VALUE — so a conversation sitting at 187 % of the window read
        // as exactly full, in the tooltip the user opens to decide whether to clear. At 100 % one
        // more turn is a question; past it the backend is already dropping the head of the
        // conversation, system prompt included, without saying so. The ring clamps its own arc
        // (RingArc) and prints this value.
        var pct   = promptTokens * 100.0 / limit;
        var color = pct < 50 ? "#606060"
                  : pct < 80 ? "#C0A000"
                  : pct < 95 ? "#D06000"
                             : "#CC2222";

        return new ContextBudget(pct, color);
    }

    /// <summary>
    /// The gauge as a ring (the chat's composer): path data for the arc of a 7-px circle centred on (9, 9), from the
    /// top, clockwise, for a share of 100 — empty at 0, just short of closing at 100 and past it.
    /// </summary>
    /// <remarks>⚠ An arc whose two ends meet draws nothing, and the end point is written to two decimals: closer to a
    /// full turn than 99.5 %, it rounds onto the start and a full window would show an EMPTY ring.</remarks>
    public static string RingArc(double percent)
    {
        var share = Math.Clamp(percent, 0, 99.5) / 100.0;
        if (share <= 0) return "M 9,2";
        var angle = share * 2 * Math.PI;
        var x = 9 + 7 * Math.Sin(angle);
        var y = 9 - 7 * Math.Cos(angle);
        return string.Create(CultureInfo.InvariantCulture,
            $"M 9,2 A 7,7 0 {(share > 0.5 ? 1 : 0)} 1 {x:0.##},{y:0.##}");
    }
}
