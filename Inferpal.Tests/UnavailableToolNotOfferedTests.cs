using System.IO;
using System.Linq;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Docs;
using Inferpal.Services.Execution;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ A tool whose every answer is "unavailable here" is not offered. With semantic search turned off and no index,
/// <c>search_codebase</c> answered every call with two remedies only the user can apply (<c>/index rebuild</c>, a
/// setting) and not the tool the model can call: small models called it again, or answered without having searched.
/// <c>search_docs</c> is the same, until the user adds documentation — most users never do. Both come back as soon as
/// they can answer, and a call made anyway still runs and says why.
/// </summary>
[Collection(SignalCollection.Name)]
public sealed class UnavailableToolNotOfferedTests : IDisposable
{
    private readonly SignalScratchDir _scratch = new();
    private readonly string _root;
    private readonly List<IDisposable> _services = [];

    public UnavailableToolNotOfferedTests()
    {
        TestRagStore.Redirect();
        _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"offered-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Pricing.cs"), string.Join('\n',
            "namespace Shop;", "public static class Pricing", "{",
            "    public static decimal ApplyLoyaltyDiscount(decimal amount, int years) => years >= 5 ? amount * 0.9m : amount;",
            "    public static decimal Round(decimal amount) => decimal.Round(amount, 2);", "}"));
    }

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        _scratch.Dispose();
    }

    private ToolRegistry Registry(ProjectIndexService index, DocsIndexService docs, InferpalConfig config,
                                  FakeInferenceProvider client)
    {
        var editor = new NullEditorSurface();
        var approval = new NoopApproval();
        return new ToolRegistry(editor, approval, config, index, client,
                                new ProjectMapService(editor, index), new McpToolService(config, approval),
                                docs, new OpenDocumentOverlay(), new NullDebugSession());
    }

    private (ProjectIndexService Index, InferpalConfig Config, FakeInferenceProvider Client) Unindexed()
    {
        var config = new InferpalConfig { RagEnabled = false };
        var client = new FakeInferenceProvider { Embedding = [0.1f, 0.2f] };
        var index = new ProjectIndexService(client, config, new LspSemanticProvider());
        _services.Add(index);
        index.SetRoot(_root);   // what both front-ends do with semantic search off: a root, no pass
        return (index, config, client);
    }

    private static List<string> Offered(ToolRegistry registry) =>
        registry.Definitions.Select(d => d.Function.Name).ToList();

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public void WithNoIndex_SearchCodebaseIsNotOffered_ButTheTextSearchIs()
    {
        var (index, config, client) = Unindexed();
        var names = Offered(Registry(index, new DocsIndexService(client, config), config, client));

        Assert.Contains("search_in_files", names);   // witness: the list is the registry's, read whole
        Assert.Contains("read_file", names);
        Assert.Equal(0, index.ChunkCount);
        Assert.DoesNotContain("search_codebase", names);
    }

    [Fact]
    public async Task AnIndexWithChunks_OffersSearchCodebase()
    {
        // Reference arm: without it, a registry that never offered the tool would pass the test above.
        var config = new InferpalConfig { RagEnabled = true };
        var client = new FakeInferenceProvider { Embedding = [0.1f, 0.2f] };
        var index = new ProjectIndexService(client, config, new LspSemanticProvider());
        _services.Add(index);
        index.StartIndexing(_root);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while ((index.IsIndexing || index.ChunkCount == 0) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(index.ChunkCount > 0, $"the pass indexed nothing: {index.Status}");

        Assert.Contains("search_codebase", Offered(Registry(index, new DocsIndexService(client, config), config, client)));
    }

    [Fact]
    public void WithNoDocumentationIndexed_SearchDocsIsNotOffered()
    {
        var (index, config, client) = Unindexed();
        var names = Offered(Registry(index, new DocsIndexService(client, config), config, client));

        Assert.Contains("search_in_files", names);
        Assert.DoesNotContain("search_docs", names);
    }

    [Fact]
    public async Task IndexedDocumentation_OffersSearchDocs()
    {
        // Reference arm.
        var (index, config, client) = Unindexed();
        var docs = new DocsIndexService(client, config)
        {
            CrawlForTests = (_, _) => Task.FromResult(new List<DocCrawler.Page>
            {
                new("https://docs.example.com/a", "Retries", "How retries work: every request is retried three times."),
            }),
        };
        await docs.AddOrReindexAsync(DocSite.Create("https://docs.example.com/", "Example"), progress: null,
                                     CancellationToken.None);
        Assert.True(docs.ChunkCount > 0, "the documentation indexed nothing");

        Assert.Contains("search_docs", Offered(Registry(index, docs, config, client)));
    }

    [Fact]
    public async Task ACallMadeAnyway_StillAnswers_AndNamesWhatTheModelCanCallInstead()
    {
        // A name read earlier in the thread is not an invented one: the call runs, and its answer carries the remedy
        // the MODEL can apply — not only the user's.
        var (index, config, client) = Unindexed();
        var registry = Registry(index, new DocsIndexService(client, config), config, client);

        var answer = await registry.ExecuteAsync("search_codebase", Args(new { query = "loyalty discount" }),
                                                 CancellationToken.None);

        Assert.DoesNotContain("Unknown tool", answer, StringComparison.Ordinal);
        Assert.Contains("/index rebuild", answer, StringComparison.Ordinal);   // witness: the not-ready answer
        Assert.Contains("search_in_files", answer, StringComparison.Ordinal);
    }
}
