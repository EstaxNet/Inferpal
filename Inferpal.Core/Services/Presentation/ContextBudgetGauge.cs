using System.Globalization;
using Inferpal.Localization;

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

        // ⚠ NOT clamped to 100: the clamp belongs to a bar, which cannot be wider than itself, and
        // it had been applied to the VALUE — so a conversation sitting at 187 % of the window read
        // as exactly full, in the tooltip the user opens to decide whether to clear. At 100 % one
        // more turn is a question; past it the backend is already dropping the head of the
        // conversation, system prompt included, without saying so. The indicator is a WPF
        // ProgressBar with Maximum=100, which coerces its own Value.
        var pct   = promptTokens * 100.0 / limit;
        var color = pct < 50 ? "#606060"
                  : pct < 80 ? "#C0A000"
                  : pct < 95 ? "#D06000"
                             : "#CC2222";

        // ⚠ This was English, with invariant separators, and the original comment justified the
        // English by the NUMBERS ("a consistent, deterministic readout") — an argument that says
        // nothing about the language. Meanwhile the VS Code panel said the same thing, translated
        // and formatted for the locale, about an element that does exactly the same thing on both
        // sides (clicking it opens the X-Ray). The sentence is now the SAME, word for word, lifted
        // from the extension's l10n bundles.
        var tooltip = Strings.ContextGaugeTooltip(
            promptTokens.ToString("N0", CultureInfo.CurrentCulture),
            limit.ToString("N0", CultureInfo.CurrentCulture),
            pct.ToString("F0", CultureInfo.CurrentCulture));

        return new ContextBudget(pct, color, tooltip);
    }
}
