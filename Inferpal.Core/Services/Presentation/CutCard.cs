using Inferpal.Localization;

namespace Inferpal.Services.Presentation;

/// <summary>
/// What a turn cut at its announced bound adds under its cause (the server stayed silent while it read the prompt): what
/// the turn wrote, and the way to the time limits — the same card in both editors. Asking again is the failed turn's own
/// Regenerate.
/// </summary>
/// <param name="Note">"Nothing was written" when every step the turn ran only reads and its run changed no file;
/// <c>null</c> otherwise — a plain question has no steps to reassure about, and a command or a server's tool may have
/// written what the run cannot count.</param>
/// <param name="OpenLimits">The button's words: it opens the settings on the page and the section of the time limits.</param>
internal sealed record CutCard(string? Note, string OpenLimits)
{
    /// <summary>The settings page that holds the time limits (<see cref="SettingsSchema"/>).</summary>
    internal const string TimeLimitsPage = "agent";

    /// <summary>The section of that page, folded by default: opened with it.</summary>
    internal const string TimeLimitsSection = "SettingsSectionTimeLimits";

    /// <summary>The card of a failed turn, or <c>null</c> when its wait did not run out (another failure has its own
    /// cause and no time limit to offer).</summary>
    /// <param name="toolsRun">The tools the turn ran, by name.</param>
    /// <param name="filesChanged">The files its run changed.</param>
    public static CutCard? Of(bool waitRanOut, IReadOnlyCollection<string> toolsRun, int filesChanged) => waitRanOut
        ? new(toolsRun.Count > 0 && filesChanged == 0 && toolsRun.All(PlanModeToolRegistry.IsAllowed)
                  ? Strings.CutNothingWritten : null,
              Strings.CutOpenTimeLimits)
        : null;
}
