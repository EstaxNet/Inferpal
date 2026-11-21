using Inferpal.Services.Execution;

namespace Inferpal.Services.Presentation;

/// <summary>What the person works on with the agent: each chosen family keeps its tools on, the others are turned off.</summary>
[Flags]
internal enum AssistantWork
{
    None = 0,
    Code = 1,
    Tests = 2,
    WebPages = 4,
    DesktopApps = 8,
    Data = 16,
    Apis = 32,
    Refactoring = 64,
    Documents = 128,
}

/// <summary>What the agent may run: free commands, builds and tests only, nothing.</summary>
internal enum AssistantCommands { Free, BuildAndTests, Nothing }

/// <summary>What the agent may reach online: search and read, only the pages it is given, nothing.</summary>
internal enum AssistantInternet { SearchAndRead, GivenPages, Never }

/// <summary>Whether the agent may drive the editor's debugger.</summary>
internal enum AssistantDebugger { May, Never }

/// <summary>How much goes with each question.</summary>
internal enum AssistantContext { Lean, Balanced, Full }

/// <summary>The person's answers to the tools assistant (pari 163).</summary>
/// <param name="McpServersOn">Each MCP server the person turned on (<c>true</c>) or off; a server not named is left as it is.</param>
internal sealed record AssistantAnswers(
    AssistantWork Work, AssistantCommands Commands, AssistantInternet Internet, AssistantDebugger Debugger,
    AssistantContext Context, IReadOnlyDictionary<string, bool> McpServersOn);

/// <summary>What a tool becomes, and the answer that decides it: <paramref name="Because"/> is the key of the sentence the
/// review shows in the person's language (<see cref="ToolsAssistant.Reasons"/>).</summary>
internal sealed record AssistantChange(string Key, bool On, string Because, int Tokens);

/// <summary>What applying the answers writes, and what the review shows before anything is written.</summary>
/// <param name="DisabledTools">The new <c>disabledTools</c>: the lines the assistant decides, every other line kept.</param>
/// <param name="AutoContext">The new <c>ragAutoContextEnabled</c>.</param>
/// <param name="Changes">Every tool whose state changes, in the order a request sends them.</param>
/// <param name="ToolTokensBefore">What the tool definitions weigh now (measured).</param>
/// <param name="ToolTokensAfter">What they weigh once applied (measured on the same definitions).</param>
/// <param name="On">How many built-in tools stay on once applied.</param>
/// <param name="Off">How many are turned off.</param>
internal sealed record AssistantPlan(string DisabledTools, bool AutoContext, IReadOnlyList<AssistantChange> Changes,
                                     int ToolTokensBefore, int ToolTokensAfter, int On, int Off);

/// <summary>
/// The tools assistant's decision (pari 163): which built-in tool stays on and which is turned off, from three answers —
/// what the person works on, what the agent may reach, what goes with each question. Pure: the two editors show it,
/// neither decides.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Two states, never "on demand": measured (pari 163), a model that does not see a tool does not load it — it does the
/// task by hand, slower and less often right. A tool turned off is reached again through the chat's notice when a question
/// needs it.
/// </para>
/// <para>
/// ⚠ "Only the pages I give it" keeps <c>fetch_url</c> on: the person's own <c>@url</c> answers to the same switch
/// (<c>ToolRegistry.PersonFetchRefusal</c>), and turning it off would refuse the page they paste.
/// </para>
/// <para>
/// ⚠ A tool the assistant does not know (added later, a custom tool) is left as it is: the assistant only writes the lines of
/// the tools it decides, every other line of the setting is kept.
/// </para>
/// </remarks>
internal static class ToolsAssistant
{
    /// <summary>The built-in tools by the family that keeps them on.</summary>
    internal static readonly IReadOnlyDictionary<string, AssistantWork> Families = new Dictionary<string, AssistantWork>(StringComparer.Ordinal)
    {
        ["view_page"] = AssistantWork.WebPages,
        ["interact_page"] = AssistantWork.WebPages,
        ["view_window"] = AssistantWork.DesktopApps,
        ["interact_window"] = AssistantWork.DesktopApps,
        ["query_database"] = AssistantWork.Data,
        ["query_data"] = AssistantWork.Data,
        ["http_request"] = AssistantWork.Apis,
        ["rename_symbol"] = AssistantWork.Refactoring,
        ["analyze_code"] = AssistantWork.Refactoring,
        ["edit_notebook"] = AssistantWork.Documents,
        ["run_notebook"] = AssistantWork.Documents,
        ["ocr"] = AssistantWork.Documents,
    };

    /// <summary>The tools turned off unless the person sends everything (<see cref="AssistantContext.Full"/>): rarely needed
    /// whatever the work.</summary>
    internal static readonly IReadOnlySet<string> Rare = new HashSet<string>(StringComparer.Ordinal)
    {
        "generate_project_map", "get_solution_info", "replace_selection", "insert_at_cursor",
    };

    /// <summary>The debugger's tools.</summary>
    internal static readonly IReadOnlySet<string> Debugger = new HashSet<string>(StringComparer.Ordinal)
    {
        "debug_control", "debug_inspect", "get_debugger_state",
    };

    /// <summary>Whether <paramref name="tool"/> stays on with <paramref name="answers"/>, and why; <c>null</c> for a tool the
    /// assistant does not decide (left as it is).</summary>
    internal static (bool On, string Because)? Decide(string tool, AssistantAnswers answers)
    {
        // The limits first: what the person said the agent may not reach.
        if (tool == "run_command")
            return answers.Commands == AssistantCommands.Free ? (true, Reasons.AssistBecauseFreeCommands) : (false, Reasons.AssistBecauseNoFreeCommands);
        if (tool is "run_tests" or "get_diagnostics")
            return answers.Commands != AssistantCommands.Nothing ? (true, Reasons.AssistBecauseBuildsAndTests) : (false, Reasons.AssistBecauseNothingRuns);
        if (tool == "web_search")
            return answers.Internet == AssistantInternet.SearchAndRead ? (true, Reasons.AssistBecauseSearchAndRead) : (false, Reasons.AssistBecauseNoSearch);
        if (tool == "fetch_url")
            return answers.Internet != AssistantInternet.Never ? (true, Reasons.AssistBecauseGivenPages) : (false, Reasons.AssistBecauseNoInternet);
        if (tool == "http_request" && answers.Internet == AssistantInternet.Never && !answers.Work.HasFlag(AssistantWork.Apis))
            return (false, Reasons.AssistBecauseNoInternet);
        if (Debugger.Contains(tool))
            return answers.Debugger == AssistantDebugger.May ? (true, Reasons.AssistBecauseDebuggerMay) : (false, Reasons.AssistBecauseNoDebugger);

        var full = answers.Context == AssistantContext.Full;
        if (Rare.Contains(tool)) return full ? (true, Reasons.AssistBecauseFull) : (false, Reasons.AssistBecauseRare);
        if (Families.TryGetValue(tool, out var family))
            return answers.Work.HasFlag(family) ? (true, Reasons.AssistBecauseYourWork)
                 : full                          ? (true, Reasons.AssistBecauseFull)
                 :                                 (false, Reasons.AssistBecauseNotYourWork);
        return null;
    }

    /// <summary>The plan for <paramref name="answers"/>, from the tools as they stand (<paramref name="profile"/>, from
    /// <c>ToolRegistry.Profile</c>) and the setting as written.</summary>
    internal static AssistantPlan Plan(AssistantAnswers answers, IReadOnlyList<ToolProfileEntry> profile, string disabledTools)
    {
        var decided = new Dictionary<string, bool>(StringComparer.Ordinal);
        var changes = new List<AssistantChange>();
        foreach (var e in profile.Where(e => e.Source == ToolSource.BuiltIn))
        {
            if (Decide(e.Key, answers) is not var (on, because)) continue;
            decided[e.Key] = on;
            // A tool not offered now (no debugger, no window) gets its line, for the day it is: no change to review.
            var nowOn = e.State != ToolState.OffByUser;
            if (nowOn != on && e.Tool.IsOffered) changes.Add(new(e.Key, on, because, e.Tokens));
        }
        // An MCP server turned off whole: every one of its tools in the setting, as /tools off writes them.
        bool Server(ToolProfileEntry e, bool on) =>
            e.Source == ToolSource.Mcp && e.Server is { } s && answers.McpServersOn.TryGetValue(s, out var v) && v == on;
        var mcpOff = profile.Where(e => Server(e, false)).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var mcpOn = profile.Where(e => Server(e, true)).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);

        // Every other line kept as written: a custom tool, a tool of the other editor, a comment.
        var disabled = Lines(disabledTools).Where(l => !decided.ContainsKey(l) && !mcpOn.Contains(l) && !mcpOff.Contains(l))
            .Concat(decided.Where(d => !d.Value).Select(d => d.Key))
            .Concat(mcpOff)
            .Distinct(StringComparer.Ordinal).ToList();

        bool SentAfter(ToolProfileEntry e) =>
            e.Tool.IsOffered
            && (decided.TryGetValue(e.Key, out var on) ? on
                : mcpOff.Contains(e.Key) ? false
                : e.State == ToolState.Sent);
        // Offered judged on the tool, never its state: one not offered that is turned off reads "off by the user".
        var offered = profile.Where(e => e.Source == ToolSource.BuiltIn && e.Tool.IsOffered).ToList();
        var offCount = offered.Count(e => decided.TryGetValue(e.Key, out var on) ? !on : e.State == ToolState.OffByUser);
        return new AssistantPlan(string.Join("\n", disabled), answers.Context != AssistantContext.Lean, changes,
                                 profile.Where(e => e.State == ToolState.Sent).Sum(e => e.Tokens),
                                 profile.Where(SentAfter).Sum(e => e.Tokens),
                                 offered.Count - offCount, offCount);
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    /// <summary>The keys of the review's "because" sentences, one per answer that decides a tool.</summary>
    internal static class Reasons
    {
        internal const string AssistBecauseFreeCommands = nameof(AssistBecauseFreeCommands);
        internal const string AssistBecauseNoFreeCommands = nameof(AssistBecauseNoFreeCommands);
        internal const string AssistBecauseBuildsAndTests = nameof(AssistBecauseBuildsAndTests);
        internal const string AssistBecauseNothingRuns = nameof(AssistBecauseNothingRuns);
        internal const string AssistBecauseSearchAndRead = nameof(AssistBecauseSearchAndRead);
        internal const string AssistBecauseNoSearch = nameof(AssistBecauseNoSearch);
        internal const string AssistBecauseGivenPages = nameof(AssistBecauseGivenPages);
        internal const string AssistBecauseNoInternet = nameof(AssistBecauseNoInternet);
        internal const string AssistBecauseDebuggerMay = nameof(AssistBecauseDebuggerMay);
        internal const string AssistBecauseNoDebugger = nameof(AssistBecauseNoDebugger);
        internal const string AssistBecauseFull = nameof(AssistBecauseFull);
        internal const string AssistBecauseRare = nameof(AssistBecauseRare);
        internal const string AssistBecauseYourWork = nameof(AssistBecauseYourWork);
        internal const string AssistBecauseNotYourWork = nameof(AssistBecauseNotYourWork);
    }
}
