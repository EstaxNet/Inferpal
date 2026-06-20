using System.Text.Json;
using Inferpal.Models;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Decides which tool-call batches the orchestrator may execute concurrently.
public class AgentOrchestratorParallelTests
{
    private static IReadOnlyList<ToolCallDto> Batch(params string[] toolNames)
    {
        var list = new List<ToolCallDto>();
        foreach (var name in toolNames)
        {
            using var doc = JsonDocument.Parse("{}");
            list.Add(new ToolCallDto(new ToolCallFunction(name, doc.RootElement.Clone())));
        }
        return list;
    }

    [Fact]
    public void MultipleReadOnlyTools_RunInParallel()
    {
        Assert.True(AgentOrchestrator.ShouldRunParallel(Batch("read_file", "read_file", "list_files")));
        Assert.True(AgentOrchestrator.ShouldRunParallel(Batch("read_file", "search_in_files")));
    }

    [Fact]
    public void SingleCall_DoesNotParallelize()
    {
        Assert.False(AgentOrchestrator.ShouldRunParallel(Batch("read_file")));
    }

    [Fact]
    public void EmptyBatch_DoesNotParallelize()
    {
        Assert.False(AgentOrchestrator.ShouldRunParallel(Batch()));
    }

    [Fact]
    public void BatchWithMutatingTool_StaysSequential()
    {
        Assert.False(AgentOrchestrator.ShouldRunParallel(Batch("read_file", "write_file")));
        Assert.False(AgentOrchestrator.ShouldRunParallel(Batch("apply_diff", "read_file")));
    }

    [Fact]
    public void GpuOrApprovalTools_AreNotParallelSafe()
    {
        // search_codebase/search_docs do GPU embeddings; fetch_url/web_search are approval-gated.
        Assert.False(AgentOrchestrator.ShouldRunParallel(Batch("read_file", "search_codebase")));
        Assert.False(AgentOrchestrator.ShouldRunParallel(Batch("fetch_url", "fetch_url")));
        Assert.False(AgentOrchestrator.ShouldRunParallel(Batch("read_file", "search_docs")));
    }

    [Fact]
    public void VsContextTools_AreNotParallelSafe()
    {
        Assert.False(AgentOrchestrator.ShouldRunParallel(Batch("get_active_document", "get_open_editors")));
    }
}
