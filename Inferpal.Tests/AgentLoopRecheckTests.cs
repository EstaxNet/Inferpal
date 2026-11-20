using System.Text.Json;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  The loop detector does not stop a tool used the way the product tells the model to use it.
//
//  It classified by tool NAME: run_command and debug_control are not observations, so their second
//  identical call anywhere in the run ended it — the second poll of a background build still running
//  ("use action='poll' to read its output"), the re-run of tsc after a fix (get_diagnostics names
//  run_command for non-.NET projects), the second step_over of a step → inspect cycle.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class AgentLoopRecheckTests
{
    private static List<ToolCallDto> Call(string name, object args) =>
        [new ToolCallDto(new ToolCallFunction(name, JsonSerializer.SerializeToElement(args)))];

    private static bool[] Run(params List<ToolCallDto>[] batches)
    {
        var counts = new Dictionary<string, int>();
        return [.. batches.Select(b => AgentLoopPolicy.IsLoop(counts, b))];
    }

    [Fact]
    public void PollingABackgroundJob_IsNotALoop()
    {
        var poll = Call("run_command", new { action = "poll", id = "bg1" });

        Assert.DoesNotContain(true, Run(poll, poll, poll, poll));
    }

    [Fact]
    public void ACheck_ReRunAfterAnEdit_IsNotALoop()
    {
        var check = Call("run_command", new { command = "npx tsc --noEmit" });
        var fix   = Call("apply_diff", new { path = "a.ts", old_content = "x", new_content = "y" });

        Assert.DoesNotContain(true, Run(check, fix, check));
    }

    [Fact]
    public void SteppingThroughTheDebugger_IsNotALoop()
    {
        var step    = Call("debug_control", new { action = "step_over" });
        var inspect = Call("debug_inspect", new { });

        Assert.DoesNotContain(true, Run(step, inspect, step, inspect, step));
    }

    [Fact]
    public void AStall_IsStillALoop()
    {
        // Reference arms: an identical write twice, and a command run again and again with nothing in between.
        var write   = Call("write_file", new { path = "a.txt", content = "x" });
        var command = Call("run_command", new { command = "npm install" });

        Assert.Equal([false, true], Run(write, write));
        Assert.Equal([false, false, true], Run(command, command, command));
    }

    private static bool[] RunWithResults(params (List<ToolCallDto> Batch, string Result)[] batches)
    {
        var counts = new Dictionary<string, int>();
        return [.. batches.Select(b =>
        {
            var loop = AgentLoopPolicy.IsLoop(counts, b.Batch);
            // One result per call; a batch of a poll and another call: the poll answered, the other failed.
            if (!loop) AgentLoopPolicy.Forgive(counts, b.Batch, b.Batch.Count == 1 ? [b.Result] : ["still running", "Error: the search failed."]);
            return loop;
        })];
    }

    private const string NotStarted = "Error: The debugging session did not start. No launch configuration.";

    [Fact]
    public void AFailedCall_RetriedOnceSomethingElseRan_IsNotALoop()
    {
        // A failure that asks for a step first: debug_control start without a launch configuration, then the launch.json
        // written, then the same start, now able to work.
        var start = Call("debug_control", new { action = "start" });
        var write = Call("write_file", new { path = ".vscode/launch.json", content = "{}" });

        Assert.Equal([false, false, false], RunWithResults((start, NotStarted), (write, "Wrote .vscode/launch.json"), (start, "Started.")));
    }

    [Fact]
    public void ARetry_IsGivenOnce_AndOnlyAfterABatchThatDidItsWork()
    {
        var start  = Call("debug_control", new { action = "start" });
        var write1 = Call("write_file", new { path = ".vscode/launch.json", content = "{1}" });
        var write2 = Call("write_file", new { path = ".vscode/launch.json", content = "{2}" });
        var read   = Call("read_file", new { path = "a.cs" });
        var poll   = Call("run_command", new { action = "poll", id = "bg1" });
        var other  = Call("web_search", new { query = "launch.json" });

        // The same failure again with nothing in between.
        Assert.Equal([false, true], RunWithResults((start, NotStarted), (start, NotStarted)));
        // Two failures taking turns: neither did any work.
        Assert.Equal([false, false, true], RunWithResults((start, NotStarted), (other, "Error: the search failed."), (start, NotStarted)));
        // A call that is not counted (a poll) is no work done for the retry.
        Assert.Equal([false, false, true], RunWithResults((start, NotStarted), (poll, "still running"), (start, NotStarted)));
        // One retry per batch: failed again after its retry, it is counted.
        Assert.Equal([false, false, false, false, true],
                     RunWithResults((start, NotStarted), (write1, "Wrote it"), (start, NotStarted), (write2, "Wrote it"), (start, NotStarted)));
        // A failure and a read taking turns: the retry is given once, then the failure is counted.
        Assert.Equal([false, false, false, false, true],
                     RunWithResults((start, NotStarted), (read, "text"), (start, NotStarted), (read, "text"), (start, NotStarted)));
        // A poll that answered beside a failing call is no work done for the retry.
        Assert.Equal([false, false, true], RunWithResults((start, NotStarted),
                                                          ([.. poll, .. other], "unused"), (start, NotStarted)));
        // A call that did its work, repeated after another call, is the wasteful repeat it always was.
        Assert.Equal([false, false, true], RunWithResults((write1, "Wrote it"), (start, "Started."), (write1, "Wrote it")));
    }

    [Fact]
    public void ACallThePersonRefused_IsNoFailureToRetry()
    {
        // The same card would come back: refused, read, and asked again verbatim is counted.
        var write = Call("write_file", new { path = "src/a.cs", content = "x" });
        var read  = Call("read_file", new { path = "src/a.cs" });

        Assert.Equal([false, false, true], RunWithResults((write, ToolFailure.CancelledByUser), (read, "text"), (write, ToolFailure.CancelledByUser)));
        Assert.True(ToolFailure.IsRefusal(Localization.Strings.DebugStartCancelled));
    }

    [Fact]
    public void StoppingABackgroundJob_IsStillCounted()
    {
        // Only the actions that advance are exempt: stopping the same job twice is a stall.
        var stop = Call("run_command", new { action = "stop", id = "bg1" });

        Assert.Contains(true, Run(stop, stop, stop));
    }
}
