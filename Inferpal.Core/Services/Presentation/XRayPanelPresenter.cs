using Inferpal.Localization;
using Inferpal.Services.Commands;
using Inferpal.Services.Prompting;

namespace Inferpal.Services.Presentation;

/// <summary>One row of the interactive Context X-Ray panel.</summary>
/// <param name="Id">Stable identity (<see cref="XRayPanelPresenter.SectionId"/>) used to toggle the section.</param>
/// <param name="Label">Localized display label (same wording as the <c>/xray</c> markdown).</param>
/// <param name="Tokens">Estimated tokens of the section (chars/4 heuristic).</param>
/// <param name="Percent">Share of the total across ALL sections — enabled or not — so bars stay
/// stable while the user toggles sections on and off.</param>
/// <param name="Content">Exact section text (trimmed of its leading separator for display).</param>
/// <param name="Enabled">Whether the section will be part of the next turn's prompt.</param>
/// <param name="CanToggle">False for the base prompt, which cannot be disabled.</param>
internal sealed record XRaySectionModel(
    string Id, string Label, int Tokens, double Percent, string Content, bool Enabled, bool CanToggle);

/// <summary>Neutral model of the X-Ray panel — rendered by the VS Remote UI and the VS Code webview.</summary>
/// <param name="Sections">All prompt layers, largest first, disabled ones included.</param>
/// <param name="TotalTokens">Estimated tokens of the ENABLED sections only (what the next turn sends).</param>
/// <param name="HistoryTokens">Estimated tokens of the conversation history.</param>
/// <param name="ContextWindow">Configured context window; ≤ 0 = none configured.</param>
/// <param name="FillPercent">(enabled + history) / window, 0 when no window is configured.</param>
/// <param name="OverheadWarning">True when the project layers (context/memory/notes/rules) weigh
/// enough to deserve a trim (see <see cref="XRayPanelPresenter"/> thresholds).</param>
/// <param name="RawPrompt">Exact system prompt the next turn will send (enabled sections, in order).</param>
internal sealed record XRayPanelModel(
    IReadOnlyList<XRaySectionModel> Sections,
    int    TotalTokens,
    int    HistoryTokens,
    int    ContextWindow,
    double FillPercent,
    bool   OverheadWarning,
    string RawPrompt);

/// <summary>
/// Builds the interactive Context X-Ray panel model (roadmap 1.2.0, V2) from the same
/// <see cref="SystemPromptBuilder.BuildSections"/> layers as the <c>/xray</c> markdown. Pure and
/// synchronous → unit-testable; both front-ends render this model without recomputing anything.
/// </summary>
internal static class XRayPanelPresenter
{
    /// <summary>Project layers counted as "overhead" for the trim warning.</summary>
    private static readonly PromptSectionKind[] OverheadKinds =
        [PromptSectionKind.ProjectContext, PromptSectionKind.Memory, PromptSectionKind.Notes, PromptSectionKind.Rules];

    // Overhead warning thresholds: with a configured window the absolute share of the window is
    // what hurts; without one, only a clearly dominant overhead is worth flagging.
    private const double OverheadWindowSharePct = 15.0;
    private const double OverheadPromptSharePct = 50.0;
    private const int    OverheadMinTokens      = 500;

    /// <summary>Stable identity of a section across rebuilds (kind + detail for file-backed layers).</summary>
    public static string SectionId(PromptSection s)
        => s.Detail is null ? s.Kind.ToString() : $"{s.Kind}|{s.Detail}";

    /// <summary>Localized display label of a section (shared with the <c>/xray</c> markdown rendering).</summary>
    public static string Label(PromptSection s) => s.Kind switch
    {
        PromptSectionKind.Base     => Strings.XrayLabelBase,
        PromptSectionKind.Persona  => Strings.XrayLabelPersona + (s.Detail is null ? "" : $" ({s.Detail})"),
        PromptSectionKind.Custom   => Strings.XrayLabelCustom,
        PromptSectionKind.Template => Strings.XrayLabelTemplate,
        PromptSectionKind.Pinned   => "📌 " + s.Detail,
        PromptSectionKind.Rules    => Strings.XrayLabelRules(s.Detail ?? "?"),
        // File-backed layers: the path is the clearest, language-neutral label.
        _                          => s.Detail ?? s.Kind.ToString(),
    };

    /// <summary>Builds the panel model.</summary>
    /// <param name="sections">System-prompt layers (<see cref="SystemPromptBuilder.BuildSections"/>).</param>
    /// <param name="disabledIds">Section ids the user switched off for the next turn (null = none).</param>
    /// <param name="historyTokens">Estimated tokens of the conversation history.</param>
    /// <param name="contextWindow">Configured context window in tokens; ≤ 0 = no limit configured.</param>
    public static XRayPanelModel Build(
        IReadOnlyList<PromptSection> sections,
        IReadOnlySet<string>?        disabledIds,
        int                          historyTokens,
        int                          contextWindow)
    {
        var sized = sections
            .Select(s => (Section: s, Id: SectionId(s), Tokens: XRayCommandHandler.EstimateTokens(s.Content)))
            .Where(x => x.Tokens > 0)
            .ToList();

        var allTokens = sized.Sum(x => x.Tokens);
        var rows = sized
            .Select(x => new XRaySectionModel(
                Id:        x.Id,
                Label:     Label(x.Section),
                Tokens:    x.Tokens,
                Percent:   allTokens > 0 ? x.Tokens * 100.0 / allTokens : 0,
                Content:   x.Section.Content.Trim(),
                Enabled:   disabledIds is null || !disabledIds.Contains(x.Id),
                CanToggle: x.Section.Kind != PromptSectionKind.Base))
            .OrderByDescending(r => r.Tokens)
            .ToList();

        var enabledTokens = rows.Where(r => r.Enabled).Sum(r => r.Tokens);
        var rawPrompt = string.Concat(sized
            .Where(x => disabledIds is null || !disabledIds.Contains(x.Id))
            .Select(x => x.Section.Content));

        var overhead = sized
            .Where(x => OverheadKinds.Contains(x.Section.Kind)
                        && (disabledIds is null || !disabledIds.Contains(x.Id)))
            .Sum(x => x.Tokens);

        var warning = contextWindow > 0
            ? overhead * 100.0 / contextWindow >= OverheadWindowSharePct
            : overhead >= OverheadMinTokens
              && enabledTokens > 0
              && overhead * 100.0 / enabledTokens >= OverheadPromptSharePct;

        var fill = contextWindow > 0
            ? Math.Min(100.0, (enabledTokens + historyTokens) * 100.0 / contextWindow)
            : 0;

        return new XRayPanelModel(rows, enabledTokens, historyTokens, contextWindow, fill, warning, rawPrompt);
    }
}
