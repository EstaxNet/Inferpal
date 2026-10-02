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
}
