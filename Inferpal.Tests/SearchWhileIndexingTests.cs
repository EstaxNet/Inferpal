using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A search during the first indexing pass says the index is partial.
//
//  The pass publishes its first file, then every twentieth: on the real-condition battery an
//  eighteen-file project answered search_codebase from ONE file ("Index: 2 chunks — RAG: 2/18"), the
//  model took the two test snippets it got for the project, and never read the file with the bug.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class SearchWhileIndexingTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"indexing-{Guid.NewGuid():N}")).FullName;
    private readonly List<ProjectIndexService> _services = [];
    private readonly ManualResetEventSlim _gate = new(false);

    public void Dispose()
    {
        _gate.Set();
        foreach (var s in _services) { try { s.Dispose(); } catch { } }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // Every member carries the class name: each chunk the pass embeds then says which file it comes from.
    private static string Class(string name) =>
        $"public class {name}\n{{\n    public int {name}One() => 1;\n    public int {name}Two() => 2;\n}}\n";

    private static Task<string> Search(SemanticSearchTool tool, string query) =>
        tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { query }), CancellationToken.None);

    [Fact]
    public async Task APassStillRunning_IsSaidAboveTheResults_AndNoLongerOnceItEnds()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "Alpha.cs"), Class("Alpha"));
        await File.WriteAllTextAsync(Path.Combine(_root, "Beta.cs"), Class("Beta"));
        // The SECOND file's embedding waits: the pass stays running with only the first file published. Which file
        // comes first is the file system's order — arbitrary under POSIX (Beta first on macOS).
        string? first = null;
        var provider = new FakeInferenceProvider
        {
            Embedding   = [0.1f, 0.2f],
            OnEmbedding = text =>
            {
                var name = text.Contains("Alpha") ? "Alpha" : "Beta";
                first ??= name;
                if (name != first) _gate.Wait(TimeSpan.FromSeconds(30));
                return [0.1f, 0.2f];
            },
        };
        var config = new InferpalConfig { RagEnabled = true, RagEmbeddingModel = "embed" };
        var index  = new ProjectIndexService(provider, config, new LspSemanticProvider());
        _services.Add(index);
        var tool = new SemanticSearchTool(index, provider, config);

        index.StartIndexing(_root);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!(index.IsIndexing && index.ChunkCount > 0 && index.PassProgress.Done == 1) && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(index.IsIndexing && index.ChunkCount > 0,          // WITNESS: mid-pass, the first file published
            $"indexing={index.IsIndexing} chunks={index.ChunkCount} progress={index.PassProgress} status={index.Status} embeds={provider.EmbeddingRequests.Count}");

        var during = await Search(tool, first!);
        Assert.StartsWith("Note: the index is still being built (1 of 2 files read so far)", during);
        Assert.Contains("search_in_files", during[..during.IndexOf("\n\n", StringComparison.Ordinal)]);

        _gate.Set();
        while (!index.Status.Contains('✅') && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Contains("✅", index.Status);                             // WITNESS: the pass ended

        // Reference arm: the same search once the pass is over says nothing more.
        Assert.DoesNotContain("still being built", await Search(tool, first!));
    }
}
