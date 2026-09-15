using System.Collections.Generic;
using System.Text.Json;
using Inferpal.Models;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

public class AgentLoopPolicyTests
{
    private static List<ToolCallDto> Batch(params (string name, string args)[] calls)
    {
        var list = new List<ToolCallDto>();
        foreach (var (name, args) in calls)
        {
            using var doc = JsonDocument.Parse(args);
            list.Add(new ToolCallDto(new ToolCallFunction(name, doc.RootElement.Clone())));
        }
        return list;
    }

    [Fact]
    public void MutatingBatch_AbortsOnFirstRepeat()
    {
        var counts = new Dictionary<string, int>();
        var batch  = Batch(("write_file", """{"path":"A.cs","content":"x"}"""));

        Assert.False(AgentLoopPolicy.IsLoop(counts, batch)); // 1st occurrence
        Assert.True(AgentLoopPolicy.IsLoop(counts, batch));  // 2nd → loop
    }

    [Fact]
    public void ReadOnlyBatch_ToleratesOneExtraRepeat()
    {
        var counts = new Dictionary<string, int>();
        var batch  = Batch(("run_tests", "{}"));

        Assert.False(AgentLoopPolicy.IsLoop(counts, batch)); // 1st
        Assert.False(AgentLoopPolicy.IsLoop(counts, batch)); // 2nd — legit re-check
        Assert.True(AgentLoopPolicy.IsLoop(counts, batch));  // 3rd → loop
    }

    [Fact]
    public void DifferentArguments_AreNotALoop()
    {
        var counts = new Dictionary<string, int>();

        Assert.False(AgentLoopPolicy.IsLoop(counts, Batch(("write_file", """{"path":"A.cs"}"""))));
        Assert.False(AgentLoopPolicy.IsLoop(counts, Batch(("write_file", """{"path":"B.cs"}"""))));
        Assert.False(AgentLoopPolicy.IsLoop(counts, Batch(("write_file", """{"path":"C.cs"}"""))));
    }

    /// <summary>
    /// The edit → verify cycle the policy says it tolerates: the count was cumulative over the whole
    /// run, so the THIRD identical <c>run_tests</c> stopped the run as a loop — right at verification
    /// time, between three different edits.
    /// </summary>
    [Fact]
    public void ReadOnlyVerify_BetweenDistinctEdits_IsNotALoop()
    {
        var counts = new Dictionary<string, int>();
        var verify = Batch(("run_tests", """{"filter":"Foo"}"""));

        for (var i = 0; i < 4; i++)
        {
            Assert.False(AgentLoopPolicy.IsLoop(counts, Batch(("apply_diff", "{\"path\":\"A.cs\",\"n\":" + i + "}"))));
            Assert.False(AgentLoopPolicy.IsLoop(counts, verify));
        }
    }

    [Fact]
    public void MixedBatchWithMutatingTool_UsesMutatingThreshold()
    {
        var counts = new Dictionary<string, int>();
        // A batch containing a mutating call is not "read-only", so it aborts on first repeat.
        var batch  = Batch(("run_tests", "{}"), ("write_file", """{"path":"A.cs"}"""));

        Assert.False(AgentLoopPolicy.IsLoop(counts, batch));
        Assert.True(AgentLoopPolicy.IsLoop(counts, batch));
    }
    // ── "Does this tool only observe?" is answered once, not twice ───────────────
    //
    // The set lived here AND in PlanModeToolRegistry, and the two had drifted by two tools. The
    // direction that hurts: a tool absent here counts as a mutation, so its SECOND identical call
    // aborts the whole run as a loop — and a new mutation resets only the read-only counts, so a
    // mutating key is never cleared and the second call anywhere in the run is enough.

    [Fact]
    public void SearchingTheSameDocumentationTwice_IsNotALoop()
    {
        var counts = new Dictionary<string, int>();
        var batch  = Batch(("search_docs", """{"query":"how to configure"}"""));

        Assert.False(AgentLoopPolicy.IsLoop(counts, batch)); // 1st
        Assert.False(AgentLoopPolicy.IsLoop(counts, batch)); // 2nd — used to abort the run
        Assert.True(AgentLoopPolicy.IsLoop(counts, batch));  // 3rd, like every other observation
    }

    [Fact]
    public void AskingAPausedDebuggerWhereItIsTwice_IsNotALoop()
    {
        // The step/inspect cycle is the whole point of the debug tools, and debug_inspect's own
        // summary calls itself "everything that observes".
        var counts = new Dictionary<string, int>();
        var batch  = Batch(("debug_inspect", """{"action":"state"}"""));

        Assert.False(AgentLoopPolicy.IsLoop(counts, batch));
        Assert.False(AgentLoopPolicy.IsLoop(counts, batch));
        Assert.True(AgentLoopPolicy.IsLoop(counts, batch));
    }

    [Fact]
    public void EveryToolPlanModeAllows_CountsAsAnObservationHere()
    {
        // The drift guard: one question, one answer. Plan mode's set is what says "this tool only
        // observes"; this policy must not keep a second opinion.
        var allowed = new[]
        {
            "read_file", "list_files", "search_in_files", "search_codebase", "search_docs",
            "get_diagnostics", "get_active_document", "get_open_editors", "get_solution_info",
            "get_git_status", "get_debugger_state", "generate_project_map", "analyze_code",
            "web_search", "fetch_url",
        };

        // Witness: the literals above must still BE plan mode's set, or this proves nothing.
        foreach (var name in allowed)
            Assert.True(Inferpal.Services.Execution.PlanModeToolRegistry.IsAllowed(name),
                        $"{name} is no longer allowed in plan mode — this guard is out of date.");

        foreach (var name in allowed)
            Assert.True(AgentLoopPolicy.IsObservation(name),
                        $"{name} only observes for plan mode but counts as a mutation for loop "
                        + "detection: its second identical call would abort the run.");
    }

    [Fact]
    public void AToolThatWrites_IsStillNotAnObservation()
    {
        // Witness on the other side: the fix must not turn everything into an observation.
        foreach (var name in new[] { "write_file", "apply_diff", "delete_file", "run_command", "update_memory" })
            Assert.False(AgentLoopPolicy.IsObservation(name), $"{name} must keep the strict threshold.");
    }
}
