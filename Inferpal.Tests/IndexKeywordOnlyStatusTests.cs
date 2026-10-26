using System.IO;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Commands;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// With semantic search off, <c>/index rebuild</c> still builds a keyword index that <c>search_codebase</c> serves:
/// <c>/index</c> reports it instead of "disabled", and the index's status names the cause it is keyword-only for.
/// </summary>
/// <remarks>
/// Right after the rebuild, <c>/index</c> answered "disabled (`ragEnabled = false`)" — a status contradicting what the
/// product was doing — and the index's own status said "no embedding model is set or installed", which sends the user
/// to install a model for an index that would still embed nothing.
/// </remarks>
public class IndexKeywordOnlyStatusTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"keyword-index-{Guid.NewGuid():N}")).FullName;
    private readonly List<IDisposable> _services = [];

    public void Dispose()
    {
        foreach (var s in _services) { try { s.Dispose(); } catch { } }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private ProjectIndexService Index(InferpalConfig config)
    {
        var index = new ProjectIndexService(new FakeInferenceProvider { Embedding = [0.1f, 0.2f] }, config, new LspSemanticProvider());
        _services.Add(index);
        return index;
    }

    [Fact]
    public async Task AKeywordIndexBuiltWithSemanticSearchOff_IsReported_WithItsCause()
    {
        File.WriteAllText(Path.Combine(_root, "pricing.py"),
            "def apply_discount(price, percent):\n    return price - price * percent / 100\n\n\ndef other():\n    return 1\n");
        var config = new InferpalConfig { RagEnabled = false };
        var index  = Index(config);

        Assert.Contains(Strings.IndexDisabled, IndexCommandHandler.Handle(index, config, ["/index"], _root));   // reference arm: nothing yet

        IndexCommandHandler.Handle(index, config, ["/index", "rebuild"], _root);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while ((index.IsIndexing || index.ChunkCount == 0) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(index.ChunkCount > 0, $"the rebuild indexed nothing: {index.Status}");

        var report = IndexCommandHandler.Handle(index, config, ["/index"], _root);
        Assert.DoesNotContain(Strings.IndexDisabled, report);
        Assert.Contains(Strings.IndexChunksLine(index.ChunkCount.ToString("N0")), report);
        Assert.Contains("semantic search is off", index.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("no embedding model", index.Status, StringComparison.Ordinal);
    }

    /// <summary>With only embedding models that search code worse than keywords alone installed, the automatic choice
    /// passes over them: the index runs on keywords, and its status names them and the family that searches better —
    /// never "no embedding model is installed" beside one that is.</summary>
    [Fact]
    public async Task OnlyFamiliesBelowKeywords_LeaveTheIndexOnKeywords_AndTheStatusNamesThem()
    {
        File.WriteAllText(Path.Combine(_root, "pricing.py"),
            "def apply_discount(price, percent):\n    return price - price * percent / 100\n\n\ndef other():\n    return 1\n");
        var config = new InferpalConfig { RagEnabled = true, RagEmbeddingModel = "" };
        var index  = new ProjectIndexService(new FakeInferenceProvider
        {
            Embedding = [0.1f, 0.2f], ModelNames = ["granite4.2:8b", "bge-m3:latest", "nomic-embed-text:latest"],
        }, config, new LspSemanticProvider());
        _services.Add(index);

        IndexCommandHandler.Handle(index, config, ["/index", "rebuild"], _root);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while ((index.IsIndexing || index.ChunkCount == 0) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(index.ChunkCount > 0, $"the rebuild indexed nothing: {index.Status}");   // witness

        Assert.Null(index.QueryEmbeddingModel);
        Assert.Contains("keyword search only: bge-m3:latest, nomic-embed-text:latest search code worse than keywords alone",
                        index.Status, StringComparison.Ordinal);
        Assert.Contains("embeddinggemma", index.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("no embedding model", index.Status, StringComparison.Ordinal);
    }

    /// <summary>A model named in the field is used as named — one an earlier first run wrote there too — and the status
    /// says when it searches code worse than keywords alone, so the choice is an informed one.</summary>
    [Theory]
    [InlineData("bge-m3:latest", true)]
    [InlineData("embeddinggemma:300m", false)]
    public async Task ANamedModelThatSearchesBelowKeywords_IsUsed_AndTheStatusSaysSo(string named, bool said)
    {
        File.WriteAllText(Path.Combine(_root, "pricing.py"),
            "def apply_discount(price, percent):\n    return price - price * percent / 100\n\n\ndef other():\n    return 1\n");
        var config = new InferpalConfig { RagEnabled = true, RagEmbeddingModel = named };
        var index  = new ProjectIndexService(new FakeInferenceProvider
        {
            Embedding = [0.1f, 0.2f], ModelNames = ["granite4.2:8b", named],
        }, config, new LspSemanticProvider());
        _services.Add(index);

        IndexCommandHandler.Handle(index, config, ["/index", "rebuild"], _root);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while ((index.IsIndexing || index.ChunkCount == 0) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(index.ChunkCount > 0, $"the rebuild indexed nothing: {index.Status}");   // witness

        Assert.Equal(named, index.QueryEmbeddingModel);
        Assert.Equal(said, index.Status.Contains($"{named} searches code worse than keywords alone", StringComparison.Ordinal));
    }
}
