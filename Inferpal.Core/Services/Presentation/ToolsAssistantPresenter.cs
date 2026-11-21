using Inferpal.Localization;
using Inferpal.Services.Execution;

namespace Inferpal.Services.Presentation;

/// <summary>One line of the review: the tools that change the same way for the same reason, and what they weigh.</summary>
/// <param name="On">Whether they come back on (<c>true</c>) or are turned off.</param>
/// <param name="Because">Why, in the person's language.</param>
/// <param name="Saves">What they weigh, written in the interface's language.</param>
internal sealed record AssistantReviewRow(bool On, IReadOnlyList<string> Tools, string Because, int Tokens, string Saves);

/// <summary>An MCP server as the assistant offers it: on or off, with what its tools weigh (<paramref name="Line"/>, written).</summary>
internal sealed record AssistantServer(string Name, int Tools, int Tokens, bool On, string Line);

/// <summary>The assistant's sentences that carry numbers, written in the interface's language (counted nouns agree): the
/// editors show them as they are.</summary>
/// <param name="Share">What the tools take of the window; empty when the window is unknown.</param>
internal sealed record AssistantTexts(string Tokens, string Counts, string Share, string ContextHint, string Applied);

/// <summary>What both editors draw for the tools assistant (pari 163): the answers as they stand, what they change, and
/// what each question then carries against the window of the model.</summary>
/// <param name="Answers">The answers that describe the settings as they are now — where the assistant opens.</param>
/// <param name="Window">The window of the chat model as last read (tokens); 0 when unknown.</param>
/// <param name="Rows">The review: one row per reason that changes something.</param>
internal sealed record ToolsAssistantModel(AssistantAnswers Answers, AssistantPlan Plan, int Window,
                                           IReadOnlyList<AssistantReviewRow> Rows, IReadOnlyList<AssistantServer> Servers,
                                           AssistantTexts Texts);

/// <summary>Builds <see cref="ToolsAssistantModel"/> from the tools as they stand: the two editors call it, neither decides.</summary>
internal static class ToolsAssistantPresenter
{
    /// <summary>The model for <paramref name="answers"/> — or, when <c>null</c>, for the answers the settings describe now.</summary>
    internal static ToolsAssistantModel Build(IReadOnlyList<ToolProfileEntry> profile, string disabledTools, bool autoContext,
                                              int window, AssistantAnswers? answers = null)
    {
        // The answers the assistant opened on: what they leave unchanged is left as it is.
        var baseline = Infer(profile, autoContext);
        answers ??= baseline;
        var plan = ToolsAssistant.Plan(answers, profile, disabledTools, baseline, autoContext);
        var rows = plan.Changes
            .GroupBy(c => (c.On, c.Because))
            .Select(g => new AssistantReviewRow(g.Key.On, [.. g.Select(c => c.Key)], Strings.ByName(g.Key.Because), g.Sum(c => c.Tokens),
                                                 N(g.Sum(c => c.Tokens))))
            .OrderBy(r => r.On).ThenByDescending(r => r.Tokens)
            .ToList();
        var servers = profile.Where(e => e.Source == ToolSource.Mcp && e.Server is not null)
            .GroupBy(e => e.Server!, StringComparer.Ordinal)
            .Select(g => new AssistantServer(g.Key, g.Count(), g.Sum(e => e.Tokens), g.Any(e => e.State != ToolState.OffByUser),
                                             Strings.AssistMcpServer(g.Count(), N(g.Sum(e => e.Tokens)))))
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
        var texts = new AssistantTexts(
            Strings.AssistTokensNow(N(plan.ToolTokensAfter), N(plan.ToolTokensBefore)),
            Strings.AssistCounts(plan.On, plan.Off),
            window > 0 ? Strings.AssistShare(N((int)Math.Round(100.0 * plan.ToolTokensAfter / window)), N(window)) : string.Empty,
            window > 0 ? Strings.AssistContextHint(N(window)) : Strings.AssistContextHintUnknown,
            Strings.AssistApplied(plan.On, plan.Off));
        return new ToolsAssistantModel(answers, plan, window, rows, servers, texts);
    }

    /// <summary>A number as the person reads it, in the interface's language.</summary>
    private static string N(int n) => n.ToString("N0", Strings.UiCulture);

    /// <summary>The answers the settings describe now: a family is picked when its tools are on, a limit set when its tool
    /// is off — so the assistant opens on what is, and "Ask me again" changes only what the person changes.</summary>
    internal static AssistantAnswers Infer(IReadOnlyList<ToolProfileEntry> profile, bool autoContext)
    {
        bool On(string tool) => profile.FirstOrDefault(e => e.Key == tool && e.Source == ToolSource.BuiltIn) is { } e && e.State != ToolState.OffByUser;
        var work = AssistantWork.Code | AssistantWork.Tests;
        foreach (var (tool, family) in ToolsAssistant.Families)
            if (On(tool)) work |= family;
        var commands = On("run_command") ? AssistantCommands.Free : On("run_tests") ? AssistantCommands.BuildAndTests : AssistantCommands.Nothing;
        var internet = On("web_search") ? AssistantInternet.SearchAndRead : On("fetch_url") ? AssistantInternet.GivenPages : AssistantInternet.Never;
        var debugger = ToolsAssistant.Debugger.Any(On) ? AssistantDebugger.May : AssistantDebugger.Never;
        // The rare tools as they are: all on, all off, or mixed (left alone) — so opened and applied unchanged, nothing moves.
        var rareOn = ToolsAssistant.Rare.Count(On);
        bool? rare = rareOn == ToolsAssistant.Rare.Count ? true : rareOn == 0 ? false : null;
        var context = !autoContext ? AssistantContext.Lean : rare == true ? AssistantContext.Full : AssistantContext.Balanced;
        var servers = profile.Where(e => e.Source == ToolSource.Mcp && e.Server is not null)
            .GroupBy(e => e.Server!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Any(e => e.State != ToolState.OffByUser), StringComparer.Ordinal);
        return new AssistantAnswers(work, commands, internet, debugger, context, servers, rare);
    }
}
