using System.Text.Json;
using Inferpal.Models;
using Inferpal.Services.Agent;
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

    [Fact]
    public void StoppingABackgroundJob_IsStillCounted()
    {
        // Only the actions that advance are exempt: stopping the same job twice is a stall.
        var stop = Call("run_command", new { action = "stop", id = "bg1" });

        Assert.Contains(true, Run(stop, stop, stop));
    }
}
