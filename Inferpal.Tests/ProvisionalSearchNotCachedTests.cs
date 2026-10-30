using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.Agent;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>search_codebase</c>'s answer while the index is not ready is true for now only: kept in the run's cache, "not
/// ready" answered every later search of the run, the index ready or not.
/// </summary>
public class ProvisionalSearchNotCachedTests
{
    [Fact]
    public async Task TheNotReadyAnswer_IsNeverServedAgain_FromTheRunsCache()
    {
        TestRagStore.Redirect();
        var config = new InferpalConfig { RagEnabled = true };
        var client = new FakeInferenceProvider { Embedding = [0.1f, 0.2f] };
        using var index = new ProjectIndexService(client, config, new LspSemanticProvider());
        var tool = new SemanticSearchTool(index, client, config);

        var notReady = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { query = "order total" }), CancellationToken.None);

        Assert.True(SemanticSearchTool.IsProvisional(notReady));                         // witness: the index is empty
        Assert.False(AgentOrchestrator.IsAnAnswer(notReady));
        Assert.True(AgentOrchestrator.IsAnAnswer("OrderService.cs:3 — public int Total() => 1;"));   // reference arm
    }
}
