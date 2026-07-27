using System.Globalization;

namespace Inferpal.Services.Presentation;

/// <summary>The context-window fill indicator's computed state.</summary>
internal sealed record ContextBudget(double FillPercent, string Color, string Tooltip);

/// <summary>
/// Context-window fill gauge: maps the measured prompt size against the configured limit to a
/// fill percentage, a severity colour (green → amber → orange → red at the 50/80/95% thresholds)
/// and an English tooltip. Extracted from the tool-window VM so the threshold logic is unit-testable.
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

        var pct   = Math.Min(100.0, promptTokens * 100.0 / limit);
        var color = pct < 50 ? "#606060"
                  : pct < 80 ? "#C0A000"
                  : pct < 95 ? "#D06000"
                             : "#CC2222";

        // Invariant: the tooltip is English ("Context:" / "tokens"), so the numbers stay invariant
        // for a consistent, deterministic readout rather than a localized thousands separator.
        var tooltip = $"Context: {promptTokens.ToString("N0", CultureInfo.InvariantCulture)} / " +
                      $"{limit.ToString("N0", CultureInfo.InvariantCulture)} tokens " +
                      $"({pct.ToString("F0", CultureInfo.InvariantCulture)}%)";

        return new ContextBudget(pct, color, tooltip);
    }
}
