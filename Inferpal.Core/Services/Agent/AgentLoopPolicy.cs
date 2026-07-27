using Inferpal.Models;

namespace Inferpal.Services.Agent;

/// <summary>
/// Shared loop-detection policy for the agent loops (orchestrated and basic).
/// </summary>
/// <remarks>
/// A verbatim repeat of a tool-call batch can mean either a genuine stall or a
/// legitimate re-check. We distinguish the two by what the batch touches:
/// <list type="bullet">
///   <item>A batch that <b>mutates</b> state (write_file, apply_diff, …) and repeats
///         identical arguments is wasteful — abort on the first repeat.</item>
///   <item>A batch made <b>only</b> of read-only / idempotent tools (run_tests,
///         read_file, get_diagnostics, …) may legitimately repeat during an
///         edit → verify → edit cycle, so it tolerates one extra repeat before
///         we call it a loop.</item>
/// </list>
/// </remarks>
internal static class AgentLoopPolicy
{
    /// <summary>
    /// Tools that observe state without changing it. Repeating these is a normal part of
    /// an edit → verify → edit cycle, so they get a higher loop-detection threshold.
    /// </summary>
    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "get_diagnostics", "run_tests", "search_codebase", "search_in_files",
        "list_files", "get_active_document", "get_open_editors", "get_solution_info",
        "get_git_status", "get_debugger_state", "generate_project_map", "analyze_code",
        "web_search", "fetch_url",
    };

    /// <summary>Stable signature of a tool-call batch (each call's name + JSON arguments).</summary>
    internal static string Signature(IReadOnlyList<ToolCallDto> calls) =>
        string.Join("|", calls.Select(c => $"{c.Function.Name}:{c.Function.Arguments}"));

    /// <summary>
    /// Records <paramref name="calls"/> in <paramref name="counts"/> and returns <c>true</c> when
    /// the batch has repeated often enough to be treated as a loop. Mutating batches abort on the
    /// first verbatim repeat (2nd occurrence); read-only-only batches tolerate one extra (3rd).
    /// </summary>
    internal static bool IsLoop(Dictionary<string, int> counts, IReadOnlyList<ToolCallDto> calls)
    {
        var sig  = Signature(calls);
        int seen = counts[sig] = counts.GetValueOrDefault(sig) + 1;
        bool readOnlyBatch = calls.All(c => ReadOnlyTools.Contains(c.Function.Name));
        return seen >= (readOnlyBatch ? 3 : 2);
    }
}
