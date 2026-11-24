using System.Text.Json.Serialization;
using Inferpal.Services.Execution;

namespace Inferpal.Services.Presentation;

/// <summary>What the person works on with the agent: each chosen family keeps its tools on, the others are turned off.</summary>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<AssistantWork>))]
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
[JsonConverter(typeof(JsonStringEnumConverter<AssistantCommands>))]
internal enum AssistantCommands { Free, BuildAndTests, Nothing }

/// <summary>What the agent may reach online: search and read, only the pages it is given, nothing.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AssistantInternet>))]
internal enum AssistantInternet { SearchAndRead, GivenPages, Never }

/// <summary>Whether the agent may drive the editor's debugger.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AssistantDebugger>))]
internal enum AssistantDebugger { May, Never }

/// <summary>How much goes with each question.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AssistantContext>))]
internal enum AssistantContext { Lean, Balanced, Full }

/// <summary>The person's answers to the tools assistant (pari 163).</summary>
/// <param name="McpServersOn">Each MCP server the person turned on (<c>true</c>) or off; a server not named is left as it is.</param>
/// <param name="RareTools">The rarely needed tools on or off; <c>null</c> leaves each as it is (some on, some off).</param>
internal sealed record AssistantAnswers(
    AssistantWork Work, AssistantCommands Commands, AssistantInternet Internet, AssistantDebugger Debugger,
    AssistantContext Context, IReadOnlyDictionary<string, bool> McpServersOn, bool? RareTools = false);

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

    /// <summary>The tools of one <paramref name="work"/> family, in the order <see cref="Families"/> lists them: the one list
    /// the assistant and the tool-off notice read.</summary>
    internal static string[] Of(AssistantWork work) => [.. Families.Where(f => f.Value == work).Select(f => f.Key)];

    /// <summary>The tools turned off unless the person sends everything (<see cref="AssistantContext.Full"/>): rarely needed
    /// whatever the work.</summary>
    internal static readonly IReadOnlySet<string> Rare = new HashSet<string>(StringComparer.Ordinal)
    {
        "generate_project_map", "get_solution_info", "msbuild_property", "replace_selection", "insert_at_cursor",
    };

    /// <summary>The tools every work needs, never decided: reading, searching and editing the code, the project's state.</summary>
    internal static readonly IReadOnlySet<string> AlwaysOn = new HashSet<string>(StringComparer.Ordinal)
    {
        "read_file", "list_files", "search_in_files", "search_codebase", "search_docs", "write_file", "apply_diff", "apply_edits",
        "replace_in_files", "delete_file", "restore_file", "get_active_document", "get_open_editors", "get_git_status",
        "git_history", "update_memory", "ask_question", "read_skill_file", "tool_search",
    };

    /// <summary>The tools a limit on reach decides (<see cref="Decide"/>): commands, builds and tests, the internet.</summary>
    internal static readonly IReadOnlySet<string> Limited = new HashSet<string>(StringComparer.Ordinal)
    {
        "run_command", "run_tests", "get_diagnostics", "web_search", "fetch_url",
    };

    /// <summary>Whether the assistant knows <paramref name="tool"/>: every built-in tool must be classed somewhere, or the
    /// assistant would leave it out of what it says a choice turns off (<c>ToolsAssistantTests.EveryBuiltInTool_IsClassified</c>).</summary>
    internal static bool Classified(string tool) =>
        AlwaysOn.Contains(tool) || Limited.Contains(tool) || Families.ContainsKey(tool) || Rare.Contains(tool) || Debugger.Contains(tool);

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

        // The rare tools answer on their own: left as they are when the person's are mixed (null).
        if (Rare.Contains(tool))
            return answers.RareTools switch
            {
                true  => (true, Reasons.AssistBecauseFull),
                false => (false, Reasons.AssistBecauseRare),
                null  => null,
            };
        // Full keeps the rare tools, never a work the person left out: the work answer alone decides a family.
        if (Families.TryGetValue(tool, out var family))
            return answers.Work.HasFlag(family) ? (true, Reasons.AssistBecauseYourWork) : (false, Reasons.AssistBecauseNotYourWork);
        return null;
    }

    /// <summary>The plan for <paramref name="answers"/>, from the tools as they stand (<paramref name="profile"/>, from
    /// <c>ToolRegistry.Profile</c>) and the setting as written.</summary>
    /// <param name="baseline">The answers the assistant opened on (what the settings describe): a tool whose decision is
    /// the same under both is left exactly as it is — so a family half turned off by hand stays so, and opened and applied
    /// unchanged the assistant changes nothing. <c>null</c>: every tool the answers speak to is decided.</param>
    /// <param name="autoContextNow">The auto-context as it is: kept unless the person moved the context answer across Lean.</param>
    internal static AssistantPlan Plan(AssistantAnswers answers, IReadOnlyList<ToolProfileEntry> profile, string disabledTools,
                                       AssistantAnswers? baseline = null, bool autoContextNow = true)
    {
        var decided = new Dictionary<string, bool>(StringComparer.Ordinal);
        var changes = new List<AssistantChange>();
        foreach (var e in profile.Where(e => e.Source == ToolSource.BuiltIn))
        {
            if (Decide(e.Key, answers) is not var (on, because)) continue;
            if (baseline is not null && Decide(e.Key, baseline) is var (was, _) && was == on) continue;
            decided[e.Key] = on;
            // A tool not offered now (no debugger, no window) gets its line, for the day it is: no change to review.
            var nowOn = e.State != ToolState.OffByUser;
            if (nowOn != on && e.Tool.IsOffered) changes.Add(new(e.Key, on, because, e.Tokens));
        }
        // An MCP server the person turned on or off — only one whose answer moved: every one of its tools in the setting
        // (as /tools off writes them), or every line naming one of them taken out, the request's name for it included.
        var servers = profile.Where(e => e.Source == ToolSource.Mcp && e.Server is not null).GroupBy(e => e.Server!, StringComparer.Ordinal);
        var mcpOff = new HashSet<string>(StringComparer.Ordinal);
        var mcpOnLines = new HashSet<string>(StringComparer.Ordinal);
        var mcpOnKeys = new HashSet<string>(StringComparer.Ordinal);
        var mcpChanges = new List<AssistantChange>();
        foreach (var g in servers)
        {
            if (!answers.McpServersOn.TryGetValue(g.Key, out var on)) continue;
            if (baseline is not null && baseline.McpServersOn.TryGetValue(g.Key, out var was) && was == on) continue;
            if (on) foreach (var e in g) { mcpOnLines.Add(e.Key); mcpOnLines.Add(e.Tool.Name); mcpOnKeys.Add(e.Key); }
            else foreach (var e in g) mcpOff.Add(e.Key);
            mcpChanges.Add(new(g.Key, on, Reasons.AssistBecauseMcp, g.Sum(e => e.Tokens)));
        }

        // Every other line kept as written: a custom tool, a tool of the other editor, a comment.
        var disabled = Lines(disabledTools).Where(l => !decided.ContainsKey(l) && !mcpOnLines.Contains(l) && !mcpOff.Contains(l))
            .Concat(decided.Where(d => !d.Value).Select(d => d.Key))
            .Concat(mcpOff)
            .Distinct(StringComparer.Ordinal).ToList();

        bool SentAfter(ToolProfileEntry e) =>
            e.Tool.IsOffered
            && (decided.TryGetValue(e.Key, out var on) ? on
                : mcpOff.Contains(e.Key) ? false
                : mcpOnKeys.Contains(e.Key) || e.State == ToolState.Sent);
        // Offered judged on the tool, never its state: one not offered that is turned off reads "off by the user".
        // Counted as the tools page counts them: on = offered and not off; off = every built-in turned off, offered or not.
        bool OffAfter(ToolProfileEntry e) => decided.TryGetValue(e.Key, out var on) ? !on : e.State == ToolState.OffByUser;
        var builtIns = profile.Where(e => e.Source == ToolSource.BuiltIn).ToList();
        var onCount = builtIns.Count(e => e.Tool.IsOffered && !OffAfter(e));
        var offCount = builtIns.Count(OffAfter);
        var autoContext = baseline is not null && (baseline.Context != AssistantContext.Lean) == (answers.Context != AssistantContext.Lean)
            ? autoContextNow : answers.Context != AssistantContext.Lean;
        return new AssistantPlan(string.Join("\n", disabled), autoContext, [.. changes, .. mcpChanges],
                                 profile.Where(e => e.State == ToolState.Sent).Sum(e => e.Tokens),
                                 profile.Where(SentAfter).Sum(e => e.Tokens),
                                 onCount, offCount);
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
        internal const string AssistBecauseMcp = nameof(AssistBecauseMcp);
    }
}
