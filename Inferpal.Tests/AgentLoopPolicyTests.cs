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

    [Fact]
    public void MixedBatchWithMutatingTool_UsesMutatingThreshold()
    {
        var counts = new Dictionary<string, int>();
        // A batch containing a mutating call is not "read-only", so it aborts on first repeat.
        var batch  = Batch(("run_tests", "{}"), ("write_file", """{"path":"A.cs"}"""));

        Assert.False(AgentLoopPolicy.IsLoop(counts, batch));
        Assert.True(AgentLoopPolicy.IsLoop(counts, batch));
    }
}
