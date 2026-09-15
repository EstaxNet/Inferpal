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
    /// Tools that observe state without changing it. Repeating these is a normal part of an
    /// edit → verify → edit cycle, so they get a higher loop-detection threshold; everything else
    /// aborts the run on its first verbatim repeat.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Derived, not listed.</b> This used to be a second hand-written set beside
    /// <see cref="PlanModeToolRegistry"/>'s — the one that already answers "does this tool only
    /// observe?" — and the two had drifted apart by two tools. Both misclassifications hurt in the
    /// same direction, the dangerous one: a tool absent here is treated as a mutation, so its
    /// <b>second identical call aborts the whole run as a loop</b>.
    /// <list type="bullet">
    /// <item><c>search_docs</c>: searching the same documentation twice stopped the run.</item>
    /// <item><c>debug_inspect</c>: its own summary says it is "everything that observes", and
    /// asking a paused debugger "where am I?" twice in one run stopped the run — in the middle of
    /// the step/inspect cycle that is the whole point of the debug tools. Worse than the first:
    /// a new mutation resets only the read-only counts, so a mutating key is never cleared and the
    /// second call anywhere in the run is enough.</item>
    /// </list>
    /// The two exceptions below are named with their reason: plan mode refuses them because they
    /// <i>execute</i> or need a live session, not because they change anything.
    /// </remarks>
    internal static bool IsObservation(string toolName) =>
        PlanModeToolRegistry.IsAllowed(toolName)
        || toolName.Equals("run_tests", StringComparison.OrdinalIgnoreCase)
        || toolName.Equals(Tools.DebugInspectTool.ToolName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Stable signature of a tool-call batch (each call's name + JSON arguments).</summary>
    internal static string Signature(IReadOnlyList<ToolCallDto> calls) =>
        string.Join("|", calls.Select(c => $"{c.Function.Name}:{c.Function.Arguments}"));

    /// <summary>Key prefix of read-only batches in the counts — a character no tool name contains.</summary>
    private const string ReadOnlyKey = "ro";

    /// <summary>
    /// Records <paramref name="calls"/> in <paramref name="counts"/> and returns <c>true</c> when
    /// the batch has repeated often enough to be treated as a loop. Mutating batches abort on the
    /// first verbatim repeat (2nd occurrence); read-only-only batches tolerate one extra (3rd).
    /// </summary>
    /// <remarks>⚠ A new (non-repeated) mutation resets the read-only counts: what a verification
    /// observes has changed, so re-running it is not a repeat. Counted over the whole run, the third
    /// identical <c>run_tests</c> of an edit → verify cycle stopped the run as a loop.</remarks>
    internal static bool IsLoop(Dictionary<string, int> counts, IReadOnlyList<ToolCallDto> calls)
    {
        bool readOnlyBatch = calls.All(c => IsObservation(c.Function.Name));
        var sig  = (readOnlyBatch ? ReadOnlyKey : string.Empty) + Signature(calls);
        int seen = counts[sig] = counts.GetValueOrDefault(sig) + 1;

        if (readOnlyBatch) return seen >= 3;
        if (seen >= 2)     return true;

        foreach (var key in counts.Keys.Where(k => k.StartsWith(ReadOnlyKey, StringComparison.Ordinal)).ToList())
            counts.Remove(key);
        return false;
    }
}
