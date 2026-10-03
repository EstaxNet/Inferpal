using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Config;
using Inferpal.Services.Agent;
using Inferpal.Services.Docs;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Ten search results of 900-character excerpts are over the context cap, and the loop cuts an over-long tool result
/// in its MIDDLE: for a list ranked by relevance that dropped the middle ranks and kept the lowest. The bodies stop
/// where the cap would cut; the rest are named by location, in rank order, said above.
/// </summary>
public sealed class RankedResultBudgetTests : IDisposable
{
    private readonly string _root;
    private readonly List<IDisposable> _services = [];

    public RankedResultBudgetTests()
    {
        TestRagStore.Redirect();
        _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"ranked-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        for (int i = 0; i < 20; i++)
            File.WriteAllText(Path.Combine(_root, "src", $"Widget{i}.cs"), LongClass($"Widget{i}"));
    }

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>A class with one method whose chunk is over the 900 characters an excerpt keeps.</summary>
    private static string LongClass(string name) =>
        $"public class {name}\n{{\n    public int Widget()\n    {{\n        var total = 0;\n" +
        string.Concat(Enumerable.Range(0, 30).Select(i => $"        total += Compute({i}, \"value number {i}\");\n")) +
        "        return total;\n    }\n}\n";

    private static JsonElement Raw(string json) => JsonDocument.Parse(json).RootElement;

    private async Task<SemanticSearchTool> CodebaseToolAsync()
    {
        var config = new InferpalConfig { RagEnabled = true, RagTopK = 5 };
        var client = new FakeInferenceProvider { Embedding = [0.1f, 0.2f] };
        var index  = new ProjectIndexService(client, config, new LspSemanticProvider());
        _services.Add(index);
        index.StartIndexing(_root);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline && (index.IsIndexing || index.ChunkCount <= 10)) await Task.Delay(50);
        Assert.True(index.ChunkCount > 10, $"only {index.ChunkCount} chunk(s): this measures nothing. {index.Status}");
        return new SemanticSearchTool(index, client, config);
    }

    [Fact]
    public async Task TenLongResults_AllReachTheModel_TheLowerRanksByLocation_NothingCutInTheMiddle()
    {
        var tool = await CodebaseToolAsync();

        var report = await tool.ExecuteAsync(Raw("""{"query":"Compute value number","top_k":10}"""), CancellationToken.None);

        Assert.Contains("## Codebase search", report);                                                   // witness
        for (var rank = 1; rank <= 10; rank++)
            Assert.Contains($"[{rank}] `", report);
        Assert.Matches(@"\(results \d+–10 are listed by location only, at the end", report);
        Assert.True(report.IndexOf("are listed by location only", StringComparison.Ordinal)
                    < report.IndexOf("### [1]", StringComparison.Ordinal), "the note qualifies the results: above them");
        Assert.Equal(report, AgentOrchestrator.CapForContext(report));
    }

    [Fact]
    public async Task AFewResults_AreAllShownInFull_WithoutANote()
    {
        var tool = await CodebaseToolAsync();

        var report = await tool.ExecuteAsync(Raw("""{"query":"Compute value number","top_k":3}"""), CancellationToken.None);

        Assert.Equal(3, Regex.Matches(report, @"^### \[\d+\]", RegexOptions.Multiline).Count);
        Assert.DoesNotContain("listed by location only", report);
    }

    [Fact]
    public async Task TenLongDocumentationResults_AllReachTheModel_NothingCutInTheMiddle()
    {
        var config = new InferpalConfig { RagTopK = 5 };
        var client = new FakeInferenceProvider { Embedding = [0.1f, 0.2f] };
        static string Page(string marker) => string.Join(" ",
            Enumerable.Range(0, 20_000).Select(i => i % 30 == 0 ? marker : $"word{i % 17}"));
        var docs = new DocsIndexService(client, config)
        {
            CrawlForTests = (_, _) => Task.FromResult(new List<DocCrawler.Page>
            {
                new("https://docs.example.com/a", "Retries",  Page("retries")),
                new("https://docs.example.com/b", "Timeouts", Page("retries")),
            }),
        };
        await docs.AddOrReindexAsync(DocSite.Create("https://docs.example.com/", "Example"), progress: null,
                                     CancellationToken.None);
        Assert.True(docs.ChunkCount > 10, $"only {docs.ChunkCount} chunk(s): this measures nothing.");

        var report = await new SearchDocsTool(docs, client, config)
            .ExecuteAsync(Raw("""{"query":"retries","top_k":10}"""), CancellationToken.None);

        for (var rank = 1; rank <= 10; rank++)
            Assert.Contains($"[{rank}] ", report);
        Assert.Contains("are listed by location only", report);
        Assert.Equal(report, AgentOrchestrator.CapForContext(report));
    }
}
