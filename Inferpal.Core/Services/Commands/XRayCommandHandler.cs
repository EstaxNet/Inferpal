using System.Text;
using Inferpal.Localization;
using Inferpal.Services.Prompting;

namespace Inferpal.Services.Commands;

/// <summary>
/// Execution logic for <c>/xray</c> — the Context X-Ray: a token breakdown of everything that
/// composes the system prompt (base, custom, pinned files, project files, scoped rules), plus the
/// conversation-history estimate and the context-window fill. Token counts use the same chars/4
/// heuristic as the rest of the app (<c>AgentOrchestrator.EstimateTokens</c>). Pure and
/// synchronous → unit-testable. Same pattern as <see cref="DiagnosticsCommandHandler"/>.
/// </summary>
internal static class XRayCommandHandler
{
    private const int BarWidth = 10;

    /// <summary>Renders the X-Ray for the given prompt composition.</summary>
    /// <param name="sections">System-prompt layers (<see cref="SystemPromptBuilder.BuildSections"/>).</param>
    /// <param name="historyTokens">Estimated tokens of the conversation history (0 when empty).</param>
    /// <param name="contextWindow">Configured context window in tokens; ≤ 0 = no limit configured.</param>
    /// <param name="ragAutoContext">Whether per-turn RAG auto-context injection is enabled.</param>
    public static string Handle(
        IReadOnlyList<PromptSection> sections,
        int  historyTokens,
        int  contextWindow,
        bool ragAutoContext)
    {
        var sized = sections
            .Select(s => (Section: s, Tokens: EstimateTokens(s.Content)))
            .Where(x => x.Tokens > 0)
            .ToList();
        var total = sized.Sum(x => x.Tokens);

        var sb = new StringBuilder();
        sb.AppendLine(Strings.XrayHeader($"~{total:N0}"));
        sb.AppendLine();
        sb.AppendLine("```");

        foreach (var (section, tokens) in sized.OrderByDescending(x => x.Tokens))
        {
            var pct = total > 0 ? tokens * 100.0 / total : 0;
            sb.Append(Bar(pct))
              .Append(' ').Append($"{pct,3:0}%")
              .Append(' ').Append($"{"~" + tokens.ToString("N0"),8}")
              .Append("  ").AppendLine(Label(section));
        }

        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("- " + Strings.XrayHistory($"~{historyTokens:N0}"));
        sb.AppendLine("- " + Strings.XrayRag(ragAutoContext ? "on" : "off"));

        if (contextWindow > 0)
        {
            var used = total + historyTokens;
            var pct  = Math.Min(100.0, used * 100.0 / contextWindow);
            sb.AppendLine("- " + Strings.XrayBudget($"~{used:N0}", $"{contextWindow:N0}", $"{pct:0}"));
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Same heuristic as <c>AgentOrchestrator.EstimateTokens</c>: ~4 characters per token.</summary>
    internal static int EstimateTokens(string text) => (text.Trim().Length + 3) / 4;

    private static string Bar(double pct)
    {
        var filled = (int)Math.Round(pct / 100.0 * BarWidth, MidpointRounding.AwayFromZero);
        filled = Math.Clamp(filled, 0, BarWidth);
        return new string('█', filled) + new string('░', BarWidth - filled);
    }

    // Labels are shared with the interactive panel so both renderings use the same wording.
    private static string Label(PromptSection s) => Presentation.XRayPanelPresenter.Label(s);
}
