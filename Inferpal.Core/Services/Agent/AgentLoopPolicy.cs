using Inferpal.Models;
using Inferpal.Services.Tools;

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
    /// ⚠ <b>Derived, not listed.</b> A second hand-written set beside
    /// <see cref="PlanModeToolRegistry"/>'s — the one that already answers "does this tool only
    /// observe?" — drifts, and the misclassification hurts in one direction only, the dangerous
    /// one: a tool absent here is treated as a mutation, so its <b>second identical call aborts the
    /// whole run as a loop</b>. Searching the same documentation twice, or asking a paused debugger
    /// "where am I?" twice, is the middle of an ordinary cycle, not a loop. ⚠ And a mutating key is
    /// never cleared — a new mutation resets only the read-only counts — so for those tools the
    /// second call anywhere in the run is enough to end it.
    /// The exceptions below are named with their reason: plan mode refuses them because they
    /// <i>execute</i> or need a live session, not because they change anything — and <c>tool_search</c> because plan mode
    /// sends no MCP tool to load; it changes which definitions go, never the workspace (a compaction that dropped its
    /// result makes the model load the same tool again).
    /// </remarks>
    internal static bool IsObservation(string toolName) =>
        PlanModeToolRegistry.IsAllowed(toolName)
        || toolName.Equals("run_tests", StringComparison.OrdinalIgnoreCase)
        || toolName.Equals(Tools.DebugInspectTool.ToolName, StringComparison.OrdinalIgnoreCase)
        || toolName.Equals("tool_search", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A call that ADVANCES something by design, so its verbatim repeat is the next step, not a stall: reading a
    /// background job's new output (<c>run_command</c> <c>poll</c>), stepping or resuming the debugger. Not counted —
    /// the run's iteration cap bounds them.
    /// </summary>
    /// <remarks>⚠ Counted, the second identical poll of a build still running ended the run — the tool's own answer says
    /// "use action='poll' to read its output" — and so did the second <c>step_over</c> of a step → inspect cycle.</remarks>
    internal static bool Advances(ToolCallDto call) => call.Function.Name.ToLowerInvariant() switch
    {
        "run_command"               => call.Function.Arguments.Keyword("action") is "poll",
        Tools.DebugControlTool.ToolName
                                    => call.Function.Arguments.Keyword("action")
                                           is "continue" or "step_over" or "step_into" or "step_out",
        _                           => false,
    };

    /// <summary>
    /// A call whose repeat after a change is a re-check: an observation, or a shell command — the tool the product
    /// itself names to re-run <c>tsc</c>, <c>mypy</c>, <c>cargo check</c> once a fix is written.
    /// </summary>
    /// <remarks>⚠ As a mutation, the second identical <c>run_command</c> anywhere in the run ended it — build, edit,
    /// build again was stopped at the build that would have said whether the fix worked.</remarks>
    private static bool RepeatsAsACheck(ToolCallDto call) =>
        IsObservation(call.Function.Name) || call.Function.Name.Equals("run_command", StringComparison.OrdinalIgnoreCase);

    /// <summary>Stable signature of a tool-call batch (each call's name + the arguments as written).</summary>
    internal static string Signature(IReadOnlyList<ToolCallDto> calls) =>
        string.Join("|", calls.Select(c => $"{c.Function.Name}:{c.Function.WrittenArguments}"));

    /// <summary>Key prefix of read-only batches in the counts — control characters, which no tool name contains.</summary>
    /// <remarks>⚠ Written as escapes: typed raw they are invisible, the key reads as the letters "ro", and "ro" alone
    /// would also prefix the key of a mutating tool such as "router__deploy" — the reset below would then clear that
    /// mutation's own count in the call that records it, and its verbatim repeat would never stop the run.</remarks>
    private const string ReadOnlyKey = "\u0001ro\u0001";

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
        if (Key(calls) is not { } sig) return false;
        bool readOnlyBatch = sig.StartsWith(ReadOnlyKey, StringComparison.Ordinal);
        int seen = counts[sig] = counts.GetValueOrDefault(sig) + 1;

        if (readOnlyBatch) return seen >= 3;
        if (seen >= 2)     return true;

        foreach (var key in counts.Keys.Where(k => k.StartsWith(ReadOnlyKey, StringComparison.Ordinal)).ToList())
            counts.Remove(key);
        return false;
    }

    /// <summary>
    /// Forgets <paramref name="calls"/> in <paramref name="counts"/> when the syntax guard refused one of them
    /// (<paramref name="results"/>): the refusal asks for the same edit again when the code is valid for the project,
    /// and that repeat, stopped as a loop before it ran, would never be written.
    /// </summary>
    /// <remarks>Never a loop for all that: within a run the guard lets an edit through once it was refused, so the repeat
    /// is written or refused for another reason, and a third identical batch counts as a repeat again. ⚠ Only a refusal
    /// that OFFERS the pass is forgiven: outside a run (a <c>/task</c>) the same edit is refused every time, and forgiven
    /// it would never be counted — the task would run to its iteration limit holding the GPU.</remarks>
    internal static void Forgive(Dictionary<string, int> counts, IReadOnlyList<ToolCallDto> calls, IEnumerable<string> results)
    {
        if (results.Any(SyntaxGuard.OffersPass) && Key(calls) is { } sig) counts.Remove(sig);
    }

    /// <summary>The batch's key in the counts — <c>null</c> when every call advances.</summary>
    private static string? Key(IReadOnlyList<ToolCallDto> calls)
    {
        var counted = calls.Where(c => !Advances(c)).ToList();
        if (counted.Count == 0) return null;
        return (counted.All(RepeatsAsACheck) ? ReadOnlyKey : string.Empty) + Signature(counted);
    }
}
