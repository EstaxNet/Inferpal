namespace Inferpal.Services.Tools;

/// <summary>
/// What each built-in tool CAN do — the union of the effects its calls may have, whatever their arguments — and the few
/// facts the modes need beside it. ONE declaration the modes derive their decisions from (plan mode, background tasks, the
/// parallel batch, and the readers that ask "does this call leave the files alone?"), in place of a list of names per mode.
/// </summary>
/// <remarks>
/// A fact, held true to the code: every registered built-in tool has an entry, and every call's own effect
/// (<see cref="ITool.EffectOf"/>, action by action) stays under its ceiling (<c>ToolCeilingsTests</c>). A name not here —
/// an MCP tool, the person's shell tool, a tool added without its line — is <see cref="ToolEffect.Unknown"/>: refused by
/// every restricted mode, never parallel, always watched. Static, because the loop policy and the cut card read it by name.
/// </remarks>
internal static class ToolCeilings
{
    /// <param name="Ceiling">Every effect the tool's calls may have.</param>
    /// <param name="ConcurrentSafe">Several calls may run at once (reads with no shared editor or GPU state).</param>
    /// <param name="ReadsLiveState">Reads what the person has in front of them NOW (the active editor, the debugger): a
    /// background task would read whatever they look at while it runs.</param>
    /// <param name="ReportsThroughABuild">Runs a build only to report its diagnostics: read like a read by the readers that
    /// ask whether a call left the files alone (the work-tree watch, the project map, the cut card, the loop policy), never
    /// by plan mode. ⚠ A build that rewrites a tracked file (a restore's lock file, a generator writing into the tree) is
    /// therefore not caught by the watch.</param>
    private sealed record Fact(ToolEffect Ceiling, bool ConcurrentSafe = false, bool ReadsLiveState = false,
                               bool ReportsThroughABuild = false);

    private const ToolEffect R = ToolEffect.Reads, W = ToolEffect.Writes, X = ToolEffect.Runs, N = ToolEffect.Network;

    private static readonly Dictionary<string, Fact> Facts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["read_file"]            = new(R, ConcurrentSafe: true),
        ["list_files"]           = new(R, ConcurrentSafe: true),
        ["search_in_files"]      = new(R, ConcurrentSafe: true),
        ["search_codebase"]      = new(R),
        ["search_docs"]          = new(R),
        ["get_solution_info"]    = new(R),
        ["get_git_status"]       = new(R),
        ["generate_project_map"] = new(R),
        ["analyze_code"]         = new(R),
        ["read_skill_file"]      = new(R),
        ["get_active_document"]  = new(R, ReadsLiveState: true),
        ["get_open_editors"]     = new(R, ReadsLiveState: true),
        ["get_debugger_state"]   = new(R, ReadsLiveState: true),
        ["fetch_url"]            = new(N),
        ["web_search"]           = new(N),
        ["write_file"]           = new(W),
        ["apply_diff"]           = new(W),
        ["apply_edits"]          = new(W),
        ["restore_file"]         = new(W),
        ["delete_file"]          = new(W),
        ["insert_at_cursor"]     = new(W),
        ["replace_selection"]    = new(W),
        ["update_memory"]        = new(W),
        ["rename_symbol"]        = new(W),
        ["get_diagnostics"]      = new(X, ReportsThroughABuild: true),
        ["run_tests"]            = new(X),
        ["run_command"]          = new(R | X),
        ["debug_inspect"]        = new(R | X),
        ["debug_control"]        = new(R | W | X),
        // It changes which tools the model can call next: unknown to the modes, as an MCP tool it would load.
        ["tool_search"]          = new(ToolEffect.Unknown),
    };

    /// <summary>The names this table declares (what <c>ToolCeilingsTests</c> holds against the registered tools).</summary>
    internal static IEnumerable<string> Declared => Facts.Keys;

    /// <summary>Every effect a call of <paramref name="name"/> may have; <see cref="ToolEffect.Unknown"/> for a name not
    /// declared.</summary>
    public static ToolEffect Of(string name) => Facts.TryGetValue(name, out var f) ? f.Ceiling : ToolEffect.Unknown;

    private static bool Within(string name, ToolEffect allowed) => (Of(name) & ~allowed) == 0;

    /// <summary>Plan mode: reads and the network (each network call keeps its card), nothing that writes or runs.</summary>
    public static bool AllowedInPlan(string name) => Within(name, ToolEffect.Reads | ToolEffect.Network);

    /// <summary>A background task: reads only, and never the state the person has in front of them while it runs.</summary>
    public static bool AllowedInBackgroundTask(string name) =>
        Within(name, ToolEffect.Reads) && !(Facts.TryGetValue(name, out var f) && f.ReadsLiveState);

    /// <summary>May run beside other calls of the same batch.</summary>
    public static bool ConcurrentSafe(string name) =>
        Facts.TryGetValue(name, out var f) && f.ConcurrentSafe;

    /// <summary>
    /// A call read as leaving the work tree's files alone: what plan mode allows, plus a build that only reports
    /// (<c>get_diagnostics</c>). Read by the work-tree watch, the project map's invalidation, the cut card and the loop policy.
    /// </summary>
    public static bool LeavesTrackedFilesAlone(string name) =>
        AllowedInPlan(name) || (Facts.TryGetValue(name, out var f) && f.ReportsThroughABuild);
}
